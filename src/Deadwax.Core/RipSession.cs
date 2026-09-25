using System.Text.Json;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;
using Deadwax.Verify;

namespace Deadwax.Core;

public sealed record RipOptions
{
    public string? Device { get; init; }
    /// Where the album is written: Artist/YEAR - Album/ under this folder.
    public required string Library { get; init; }
    /// The library whose artist spellings are followed (usually ~/Music, also
    /// when writing a scratch copy somewhere else).
    public required string ConventionsLibrary { get; init; }
    public string? ReleaseId { get; init; }
    /// The folder year. Default: LibraryConventions.DefaultYear (spec §7).
    public string? Year { get; init; }
    public int? Offset { get; init; }
    public int MaxRetries { get; init; } = SecureReader.DefaultMaxRetries;
    /// Reads of a track, in all, while looking for two that agree.
    public int MaxPasses { get; init; } = 5;
    public bool AccurateRip { get; init; } = true;
}

public sealed class RipException(string message) : Exception(message);

/// The disc is on more than one release and none was named.
public sealed class ReleaseChoiceNeeded(IReadOnlyList<ReleaseCandidate> candidates)
    : Exception($"This disc is on {candidates.Count} releases; name one.")
{
    public IReadOnlyList<ReleaseCandidate> Candidates { get; } = candidates;
}

public sealed record RippedTrack(int Number, string File, bool CopyOk, int Passes, uint Crc, AccurateRipVerdict AccurateRip);

public sealed record RipResult(string AlbumDirectory, IReadOnlyList<RippedTrack> Tracks)
{
    public bool AllOk => Tracks.All(t => t.CopyOk);
}

/// One rip, start to finish, for the command line now and the window later
/// (spec §4). Reads the disc, finds the release, reads every track securely
/// until two passes agree, checks AccurateRip, and writes FLACs plus the .toc,
/// .cue, .m3u and .log. It refuses to write into a folder that exists.
public sealed class RipSession(RipOptions options, Action<string> say, Action<int, string, int, int>? progress = null)
{
    public const string Version = "0.1";

    public async Task<RipResult> RunAsync(CancellationToken ct = default)
    {
        var device = options.Device ?? CdDrive.DefaultDevice;

        DriveIdentity? identity;
        Toc toc;
        using (var drive = CdDrive.Open(device))
        {
            identity = drive.ReadIdentity();
            toc = drive.ReadToc();
        }
        var offset = options.Offset
                     ?? (identity is null ? null : WhipperConfig.ReadOffset(identity.Vendor, identity.Model, identity.Revision))
                     ?? throw new RipException($"No read offset is known for {identity?.ToString() ?? device}.");

        var discId = DiscIds.MusicBrainz(toc);
        var cddb = DiscIds.Cddb(toc);
        say($"Disc {discId}, {toc.Tracks.Count(t => t.IsAudio)} audio tracks. Reading ISRCs, CD-Text and pregaps (about two minutes)...");
        var cdrdao = await Cdrdao.ReadTocAsync(device, ct);
        var cdrdaoToc = cdrdao.Toc.ToToc();
        if (!cdrdaoToc.Tracks.SequenceEqual(toc.Tracks) || cdrdaoToc.LeadoutLsn != toc.LeadoutLsn)
            throw new RipException("cdrdao and libcdio disagree about the TOC; not ripping.");

        using var mb = new MusicBrainzClient(MusicBrainzClient.DefaultCacheDir);
        var releaseId = options.ReleaseId ?? await PickReleaseAsync(mb, discId, ct);
        using var release = await mb.GetReleaseAsync(releaseId, ct) ?? throw new RipException($"MusicBrainz has no release {releaseId}.");
        var root = release.RootElement;
        var medium = ReleaseTags.Medium(root, discId);

        var folders = await ArtistFolders.ScanAsync(options.ConventionsLibrary);
        var albumArtistId = root.GetProperty("artist-credit")[0].GetProperty("artist").GetProperty("id").GetString()!;
        var folderArtist = folders.For(albumArtistId);
        var year = options.Year ?? LibraryConventions.DefaultYear(root) ?? throw new RipException("No year known; give one.");
        var discTitle = ReleaseChoice.DiscTitle(root, medium);

        var sample = ReleaseTags.ForTrack(root, medium, toc.FirstTrack, discId, null);
        var albumArtist = folderArtist ?? sample.First(t => t.Key == "ALBUMARTIST").Value;
        var albumDir = Path.Combine(options.Library, FileNames.ArtistFolder(albumArtist), FileNames.AlbumFolder(year, discTitle));
        if (Directory.Exists(albumDir)) throw new RipException($"{albumDir} already exists; not writing over it.");
        say($"{albumArtist} - {discTitle} ({year}) -> {albumDir}");

        IReadOnlyList<AccurateRipBlock>? arBlocks = null;
        var arId = AccurateRipId.From(toc, cddb);
        if (options.AccurateRip)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Deadwax/{Version}");
                arBlocks = await AccurateRipDatabase.FetchAsync(http, arId, ct);
                say(arBlocks is null ? "AccurateRip: not in the database." : $"AccurateRip: {arBlocks.Count} pressing(s) on file.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                say($"AccurateRip unreachable ({e.Message}); ripping without it.");
            }
        }

        Directory.CreateDirectory(albumDir);
        var audioTracks = toc.IdTracks.Where(t => t.IsAudio).ToList();
        var ripped = new List<RippedTrack>();
        var logTracks = new List<LogTrack>();
        var sidecarTracks = new List<SidecarTrack>();

        using (var reader = SecureReader.Open(device))
        {
            foreach (var track in audioTracks)
            {
                ct.ThrowIfCancellationRequested();
                var n = track.Number;
                var sectors = toc.EndLsn(track) - track.StartLsn;
                var cdTrack = cdrdao.Toc.Tracks.First(t => t.Number == n);

                // Read until two passes agree: the first is the test, each later
                // one a copy compared with the pass before it.
                var (audio, report) = reader.ReadTrack(toc, n, offset, options.MaxRetries, (d, t) => progress?.Invoke(n, "test", d, t), ct);
                var testCrc = AudioChecks.Crc32(audio);
                var previousCrc = testCrc;
                uint copyCrc = 0;
                var passes = 1;
                var agreed = false;
                var repairs = report.Repairs;
                var skips = report.Skips;
                var elapsed = report.Elapsed;
                while (passes < options.MaxPasses)
                {
                    (audio, report) = reader.ReadTrack(toc, n, offset, options.MaxRetries, (d, t) => progress?.Invoke(n, "copy", d, t), ct);
                    passes++;
                    copyCrc = AudioChecks.Crc32(audio);
                    repairs += report.Repairs;
                    skips += report.Skips;
                    elapsed = report.Elapsed;
                    if (copyCrc == previousCrc && report.Skips == 0) { agreed = true; break; }
                    say($"Track {n}: pass {passes} differs from pass {passes - 1}; reading again.");
                    previousCrc = copyCrc;
                }
                if (agreed) testCrc = copyCrc;   // the pass it agreed with

                var (v1, v2) = AccurateRipChecksum.Compute(audio, n == audioTracks[0].Number, n == audioTracks[^1].Number);
                var verdict = AccurateRipMatch.For(arBlocks, audioTracks.IndexOf(track), n, v1, v2);

                var tags = LibraryConventions.Apply(
                    ReleaseTags.ForTrack(root, medium, n, discId, cdTrack.Isrc ?? cdTrack.Text?.Isrc), year, folderArtist);
                var title = tags.First(t => t.Key == "TITLE").Value;
                var artist = tags.FirstOrDefault(t => t.Key == "ARTIST")?.Value;
                var fileName = FileNames.Track(albumArtist, n, title);
                var path = Path.Combine(albumDir, fileName);

                FlacWriter.Encode(path, audio);
                FlacWriter.WriteTags(path, tags);
                if (FlacInfo.AudioMd5(path) != AudioChecks.Md5(audio))
                    throw new RipException($"{fileName}: the FLAC's stored MD5 does not match the audio read. Not continuing.");

                var status = agreed ? "Copy OK" : "Copy NOT OK";
                var seconds = sectors / (double)Toc.SectorsPerSecond;
                logTracks.Add(new LogTrack(
                    n, Path.GetRelativePath(options.Library, path), Peak(audio), cdTrack.PreEmphasis,
                    seconds / Math.Max(elapsed.TotalSeconds, 0.001), testCrc, copyCrc, repairs, skips,
                    Ar(arBlocks, audioTracks.IndexOf(track), v1, verdict.V1Confidence),
                    Ar(arBlocks, audioTracks.IndexOf(track), v2, verdict.V2Confidence),
                    status));
                sidecarTracks.Add(new SidecarTrack(n, fileName, title, artist, sectors));
                ripped.Add(new RippedTrack(n, path, agreed, passes, copyCrc, verdict));
                say($"Track {n}: {status}, {passes} pass(es), {(verdict.IsAccurate ? $"accurate ({verdict.Confidence} rips)" : arBlocks is null ? "not in AccurateRip" : "no AccurateRip match")}");
            }
        }

        var baseName = $"{FileNames.Safe(albumArtist)} - {FileNames.Safe(discTitle)}";
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".toc"), cdrdao.Text, ct);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".cue"),
            Sidecars.Cue(cdrdao.Toc, cddb, albumArtist, sample.First(t => t.Key == "ALBUM").Value, sidecarTracks, Version), ct);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".m3u"), Sidecars.M3u(sidecarTracks), ct);

        var driveName = identity is null ? device : $"{identity.Vendor} {identity.Model} (revision {identity.Revision})";
        var logDisc = new LogDisc(driveName, CdDrive.EngineDescription, offset, cdrdao.Version, false,
            albumArtist, discTitle, cddb, discId, DiscIds.MusicBrainzAttachUrl(toc), releaseId, toc);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".log"),
            RipLog.Write(logDisc, logTracks, Version, DateTimeOffset.UtcNow), ct);

        // cover.jpg, as whipper's -C file wrote it; never embedded.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Deadwax/{Version}");
            var rgId = root.TryGetProperty("release-group", out var rg) ? rg.GetProperty("id").GetString() : null;
            var cover = await CoverArt.FrontAsync(http, releaseId, rgId, ct);
            if (cover is null) say("No front cover in the Cover Art Archive.");
            else await File.WriteAllBytesAsync(Path.Combine(albumDir, "cover.jpg"), cover, ct);
        }
        catch (HttpRequestException e)
        {
            say($"Front cover not fetched ({e.Message}).");
        }

        return new RipResult(albumDir, ripped);
    }

    private async Task<string> PickReleaseAsync(MusicBrainzClient mb, string discId, CancellationToken ct)
    {
        using var disc = await mb.GetDiscAsync(discId, ct)
            ?? throw new RipException($"Disc {discId} is not in MusicBrainz. Attach it there, or give a release with --release.");
        var candidates = ReleaseChoice.FromDiscLookup(disc.RootElement, discId);
        return candidates.Count switch
        {
            0 => throw new RipException($"Disc {discId} is in MusicBrainz but on no release."),
            1 => candidates[0].Id,
            _ => throw new ReleaseChoiceNeeded(candidates),
        };
    }

    private static LogAccurateRip Ar(IReadOnlyList<AccurateRipBlock>? blocks, int index, uint crc, int confidence)
    {
        var inDatabase = blocks is not null && blocks.Any(b => index < b.Tracks.Count);
        uint? remote = confidence > 0 ? crc : null;
        return new LogAccurateRip(inDatabase, crc, confidence, remote);
    }

    /// Largest sample magnitude as a fraction of full scale, as whipper reports it.
    private static double Peak(ReadOnlySpan<byte> audio)
    {
        var max = 0;
        for (var i = 0; i + 1 < audio.Length; i += 2)
        {
            var s = Math.Abs((int)(short)(audio[i] | audio[i + 1] << 8));
            if (s > max) max = s;
        }
        return max / 32768.0;
    }
}
