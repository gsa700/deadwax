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
    /// The folder year. Default: ReleasePlan.DefaultYear (spec §7).
    public string? Year { get; init; }
    public int? Offset { get; init; }
    public int MaxRetries { get; init; } = SecureReader.DefaultMaxRetries;
    /// Reads of a track, in all, while looking for two that agree.
    public int MaxPasses { get; init; } = 5;
    public bool AccurateRip { get; init; } = true;
    /// Put MusicBrainz's disambiguation note in the folder name, as whipper did.
    public bool KeepDisambiguation { get; init; }
    /// Cap the drive at this many times 1x, for a disc that vibrates (spec §8).
    /// Null = the drive's own choice.
    public int? SpeedLimit { get; init; }
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

/// Everything known about the disc before a release is chosen: what the Disc
/// screen shows while he picks.
/// Cdrdao is null until the subchannel has been read (about two minutes):
/// the window shows the disc and its releases before that, and ripping waits
/// for it.
public sealed record PreparedDisc(
    string Device, DriveIdentity? Identity, Toc Toc, int Offset, string DiscId, uint Cddb,
    Cdrdao.Result? Cdrdao, IReadOnlyList<ReleaseCandidate> Candidates, int? SpeedLimit = null)
{
    public int AudioTracks => Toc.Tracks.Count(t => t.IsAudio);
    public string? Catalog => Cdrdao?.Toc.EffectiveCatalog;
    public int PreTrackGap => Cdrdao is { } c && c.Toc.Tracks.Count > 0 ? c.Toc.Tracks[0].PregapSectors : 0;
    public bool PreEmphasis => Cdrdao?.Toc.Tracks.Any(t => t.PreEmphasis) == true;
}

public sealed record PlannedTrack(int Number, string Title, string? Artist, int Sectors, string FileName);

/// One release chosen for the disc: the names, the years to choose between, and
/// the tags waiting for each track.
public sealed class ReleasePlan : IDisposable
{
    internal JsonDocument Document { get; }
    public JsonElement Root => Document.RootElement;
    public JsonElement Medium { get; }

    /// Null for a disc he described himself (SelfDescribed): no release exists.
    public string? ReleaseId { get; }
    public bool IsSelfDescribed => ReleaseId is null;
    /// The disc ID is not attached to this release; its medium was found by
    /// track count and lengths instead.
    public bool MatchedByTracks { get; init; }
    public string? ReleaseGroupId { get; }
    public string AlbumArtist { get; }
    public string? FolderArtist { get; }
    public string Album { get; }
    /// MusicBrainz's note telling this release from others of the same name,
    /// and whether it goes into the folder name (off unless asked for).
    public string? Disambiguation { get; }
    public bool KeepDisambiguation { get; set; }
    public string DiscTitle => ReleaseChoice.DiscTitle(Root, Medium, KeepDisambiguation);
    public int MediaCount { get; }
    public bool IsCompilation { get; }
    /// The edition's own year, and the album's first release.
    public string? EditionYear { get; }
    public string? OriginalYear { get; }
    /// The year offered first (spec §7): original for albums, edition for compilations.
    public string DefaultYear { get; }
    public IReadOnlyList<PlannedTrack> Tracks { get; }

    internal ReleasePlan(JsonDocument doc, JsonElement medium, string? folderArtist, PreparedDisc disc)
    {
        Document = doc;
        Medium = medium;
        var root = doc.RootElement;
        ReleaseId = root.TryGetProperty("id", out var idp) ? idp.GetString() : null;
        var rg = root.TryGetProperty("release-group", out var g) ? g : default;
        ReleaseGroupId = rg.ValueKind == JsonValueKind.Object ? rg.GetProperty("id").GetString() : null;
        IsCompilation = rg.ValueKind == JsonValueKind.Object && rg.TryGetProperty("secondary-types", out var types) &&
                        types.EnumerateArray().Any(t => t.GetString() == "Compilation");
        MediaCount = root.GetProperty("media").GetArrayLength();

        var sample = ReleaseTags.ForTrack(root, medium, disc.Toc.FirstTrack, disc.DiscId, null);
        FolderArtist = folderArtist;
        AlbumArtist = folderArtist ?? sample.First(t => t.Key == "ALBUMARTIST").Value;
        Album = sample.First(t => t.Key == "ALBUM").Value;
        Disambiguation = ReleaseChoice.Disambiguation(root);

        EditionYear = root.TryGetProperty("date", out var d) && d.GetString() is { Length: >= 4 } date ? date[..4] : null;
        OriginalYear = rg.ValueKind == JsonValueKind.Object && rg.TryGetProperty("first-release-date", out var f) &&
                       f.GetString() is { Length: >= 4 } first ? first[..4] : null;
        DefaultYear = LibraryConventions.DefaultYear(root) ?? EditionYear ?? OriginalYear ?? "0000";

        var tracks = new List<PlannedTrack>();
        foreach (var t in disc.Toc.IdTracks.Where(t => t.IsAudio))
        {
            var tags = ReleaseTags.ForTrack(root, medium, t.Number, disc.DiscId, null);
            var title = tags.First(x => x.Key == "TITLE").Value;
            var artist = tags.FirstOrDefault(x => x.Key == "ARTIST")?.Value;
            tracks.Add(new PlannedTrack(t.Number, title, artist, disc.Toc.EndLsn(t) - t.StartLsn,
                FileNames.Track(AlbumArtist, t.Number, title)));
        }
        Tracks = tracks;
    }

    public string AlbumDirectory(string library, string year) =>
        Path.Combine(library, FileNames.ArtistFolder(AlbumArtist), FileNames.AlbumFolder(year, DiscTitle));

    public void Dispose() => Document.Dispose();
}

/// What a rip reports as it goes. All calls come from the ripping thread.
public interface IRipObserver
{
    void Say(string message);
    void TrackStarted(int track) { }
    void Progress(int track, string pass, int done, int total);
    void PassFinished(int track, string pass, ReadReport report) { }
    void TrackFinished(RippedTrack track) { }
}

/// One rip, in three steps the window can pause between (spec §4):
///   Prepare: TOC, cdrdao, the releases the disc is on;
///   Plan: one release, with names, years and tags;
///   Rip: every track read until two passes agree, AccurateRip, FLAC, .toc,
///   .cue, .m3u, .log and cover.jpg. It refuses to write into a folder that
///   exists. The command line runs all three in a row (RunAsync).
public sealed class RipSession
{
    public const string Version = "0.1";

    public static async Task<PreparedDisc> PrepareAsync(
        string? device, int? offsetOverride, Action<string> say, bool readSubchannel = true,
        int? speedLimit = null, CancellationToken ct = default)
    {
        device ??= CdDrive.DefaultDevice;
        DriveIdentity? identity;
        Toc toc;
        using (var drive = CdDrive.Open(device))
        {
            // Before anything spins the disc up: the cap is what keeps an
            // unbalanced disc readable, and the subchannel pass is at full speed.
            if (speedLimit is { } s) drive.LimitSpeed(s);
            identity = drive.ReadIdentity();
            toc = drive.ReadToc();
        }
        var offset = offsetOverride
                     ?? (identity is null ? null : WhipperConfig.ReadOffset(identity.Vendor, identity.Model, identity.Revision))
                     ?? (identity is null ? null : DriveOffsets.ReadOffset(identity.Vendor, identity.Model, identity.Revision))
                     ?? throw new RipException(identity is null
                         ? $"{device} did not say what drive it is, so no read offset can be looked up; give one with --offset."
                         : DriveOffsets.Advice(identity.Vendor, identity.Model, identity.Revision));

        var discId = DiscIds.MusicBrainz(toc);
        var cddb = DiscIds.Cddb(toc);
        using var mb = new MusicBrainzClient(MusicBrainzClient.DefaultCacheDir);
        using var lookup = await mb.GetDiscAsync(discId, ct);
        var candidates = lookup is null ? [] : ReleaseChoice.FromDiscLookup(lookup.RootElement, discId);
        var disc = new PreparedDisc(device, identity, toc, offset, discId, cddb, null, candidates, speedLimit);
        if (!readSubchannel) return disc;
        say($"Disc {discId}, {disc.AudioTracks} audio tracks. Reading ISRCs, CD-Text and pregaps ({(speedLimit is null ? "about two minutes" : $"longer at {speedLimit}x")})...");
        return await ReadSubchannelAsync(disc, ct);
    }

    /// cdrdao's pass over the subchannel: ISRCs, catalog, CD-Text and pregaps
    /// (spec §4). Its TOC must agree with libcdio's, or nothing is ripped.
    public static async Task<PreparedDisc> ReadSubchannelAsync(PreparedDisc disc, CancellationToken ct = default)
    {
        var cdrdao = await Cdrdao.ReadTocAsync(disc.Device, ct);
        var cdrdaoToc = cdrdao.Toc.ToToc();
        if (!cdrdaoToc.Tracks.SequenceEqual(disc.Toc.Tracks) || cdrdaoToc.LeadoutLsn != disc.Toc.LeadoutLsn)
            throw new RipException("cdrdao and libcdio disagree about the TOC; not ripping.");
        return disc with { Cdrdao = cdrdao };
    }

    /// A plan from his own description of the disc (a bootleg, a private
    /// pressing, anything MusicBrainz lacks): the same tags and files as a
    /// release gives, minus every MusicBrainz ID. The description is shaped
    /// like a one-medium release so ReleaseTags reads it unchanged.
    public static ReleasePlan PlanSelfDescribed(PreparedDisc disc, DiscDescription description)
    {
        var audio = disc.Toc.IdTracks.Where(t => t.IsAudio).ToList();
        if (description.Titles.Count != audio.Count)
            throw new RipException($"The description has {description.Titles.Count} titles for {audio.Count} audio tracks.");
        var credit = new[] { new { name = description.Artist, joinphrase = "", artist = new { } } };
        var release = new
        {
            title = description.Album,
            date = description.Year,
            status = "Self-described",
            artistCredit = credit,
            media = new[]
            {
                new
                {
                    position = 1,
                    trackCount = audio.Count,
                    tracks = audio.Select((t, i) => new
                    {
                        position = t.Number,
                        title = description.Titles[i],
                        artistCredit = credit,
                        recording = new { },
                    }).ToArray(),
                },
            },
        };
        var json = JsonSerializer.Serialize(release, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower });
        var doc = JsonDocument.Parse(json);
        return new ReleasePlan(doc, doc.RootElement.GetProperty("media")[0], null, disc);
    }

    public static async Task<ReleasePlan> PlanAsync(PreparedDisc disc, string releaseId, string conventionsLibrary, CancellationToken ct = default)
    {
        using var mb = new MusicBrainzClient(MusicBrainzClient.DefaultCacheDir);
        var doc = await mb.GetReleaseAsync(releaseId, ct) ?? throw new RipException($"MusicBrainz has no release {releaseId}.");
        try
        {
            var root = doc.RootElement;
            // A release he picked by hand may not have this disc ID attached;
            // then the disc's own track lengths find it (and must, or it's the
            // wrong release).
            var attached = ReleaseTags.FindMedium(root, disc.DiscId);
            var medium = attached ?? ReleaseTags.MediumByTracks(root, disc.Toc);
            var folders = await ArtistFolders.ScanAsync(conventionsLibrary);
            var albumArtistId = root.GetProperty("artist-credit")[0].GetProperty("artist").GetProperty("id").GetString()!;
            return new ReleasePlan(doc, medium, folders.For(albumArtistId), disc) { MatchedByTracks = attached is null };
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    public static async Task<RipResult> RipAsync(
        PreparedDisc disc, ReleasePlan plan, string year, string library, IRipObserver observer,
        int maxRetries = SecureReader.DefaultMaxRetries, int maxPasses = 5, bool accurateRip = true,
        byte[]? cover = null, CancellationToken ct = default)
    {
        var cdrdao = disc.Cdrdao ?? throw new RipException("The disc's subchannel has not been read yet.");
        var root = plan.Root;
        var toc = disc.Toc;
        var albumDir = plan.AlbumDirectory(library, year);
        if (Directory.Exists(albumDir)) throw new RipException($"{albumDir} already exists; not writing over it.");
        observer.Say($"{plan.AlbumArtist} - {plan.DiscTitle} ({year}) -> {albumDir}");

        IReadOnlyList<AccurateRipBlock>? arBlocks = null;
        if (accurateRip)
        {
            try
            {
                using var http = Http(20);
                arBlocks = await AccurateRipDatabase.FetchAsync(http, AccurateRipId.From(toc, disc.Cddb), ct);
                observer.Say(arBlocks is null ? "AccurateRip: not in the database." : $"AccurateRip: {arBlocks.Count} pressing(s) on file.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                observer.Say($"AccurateRip unreachable ({e.Message}); ripping without it.");
            }
        }

        Directory.CreateDirectory(albumDir);

        // cover.jpg first, as whipper's -C file wrote it (never embedded): the
        // album is visible in AlbumWall while it rips, and he listens along.
        cover ??= await FrontCoverAsync(plan, ct);
        if (cover is null) observer.Say("No front cover in the Cover Art Archive.");
        else await File.WriteAllBytesAsync(Path.Combine(albumDir, "cover.jpg"), cover, ct);

        var audioTracks = toc.IdTracks.Where(t => t.IsAudio).ToList();
        var ripped = new List<RippedTrack>();
        var logTracks = new List<LogTrack>();
        var sidecarTracks = new List<SidecarTrack>();

        // Set again here: the cap is lost if the disc was taken out and put
        // back between the scan and the rip.
        if (disc.SpeedLimit is { } speed)
        {
            CdDrive.LimitSpeed(disc.Device, speed);
            observer.Say($"Drive speed limited to {speed}x.");
        }

        using (var reader = SecureReader.Open(disc.Device))
        {
            foreach (var track in audioTracks)
            {
                ct.ThrowIfCancellationRequested();
                var n = track.Number;
                var sectors = toc.EndLsn(track) - track.StartLsn;
                var cdTrack = cdrdao.Toc.Tracks.First(t => t.Number == n);
                observer.TrackStarted(n);

                // Read until two passes agree: the first is the test, each later
                // one a copy compared with the pass before it.
                var (audio, report) = reader.ReadTrack(toc, n, disc.Offset, maxRetries, (d, t) => observer.Progress(n, "test", d, t), ct);
                observer.PassFinished(n, "test", report);
                var testCrc = AudioChecks.Crc32(audio);
                var previousCrc = testCrc;
                uint copyCrc = 0;
                var passes = 1;
                var agreed = false;
                var repairs = report.Repairs;
                var skips = report.Skips;
                var elapsed = report.Elapsed;
                while (passes < maxPasses)
                {
                    (audio, report) = reader.ReadTrack(toc, n, disc.Offset, maxRetries, (d, t) => observer.Progress(n, "copy", d, t), ct);
                    observer.PassFinished(n, "copy", report);
                    passes++;
                    copyCrc = AudioChecks.Crc32(audio);
                    repairs += report.Repairs;
                    skips += report.Skips;
                    elapsed = report.Elapsed;
                    if (copyCrc == previousCrc && report.Skips == 0) { agreed = true; break; }
                    observer.Say($"Track {n}: pass {passes} differs from pass {passes - 1}; reading again.");
                    previousCrc = copyCrc;
                }
                if (agreed) testCrc = copyCrc;   // the pass it agreed with

                var index = audioTracks.IndexOf(track);
                var (v1, v2) = AccurateRipChecksum.Compute(audio, n == audioTracks[0].Number, n == audioTracks[^1].Number);
                var verdict = AccurateRipMatch.For(arBlocks, index, n, v1, v2);

                var tags = LibraryConventions.Apply(
                    ReleaseTags.ForTrack(root, plan.Medium, n, disc.DiscId, cdTrack.Isrc ?? cdTrack.Text?.Isrc), year, plan.FolderArtist);
                var title = tags.First(t => t.Key == "TITLE").Value;
                var artist = tags.FirstOrDefault(t => t.Key == "ARTIST")?.Value;
                var fileName = FileNames.Track(plan.AlbumArtist, n, title);
                var path = Path.Combine(albumDir, fileName);

                FlacWriter.Encode(path, audio);
                FlacWriter.WriteTags(path, tags);
                if (FlacInfo.AudioMd5(path) != AudioChecks.Md5(audio))
                    throw new RipException($"{fileName}: the FLAC's stored MD5 does not match the audio read. Not continuing.");

                var status = agreed ? "Copy OK" : "Copy NOT OK";
                var seconds = sectors / (double)Toc.SectorsPerSecond;
                logTracks.Add(new LogTrack(
                    n, Path.GetRelativePath(library, path), Peak(audio), cdTrack.PreEmphasis,
                    seconds / Math.Max(elapsed.TotalSeconds, 0.001), testCrc, copyCrc, repairs, skips,
                    Ar(arBlocks, index, v1, verdict.V1Confidence), Ar(arBlocks, index, v2, verdict.V2Confidence),
                    status));
                sidecarTracks.Add(new SidecarTrack(n, fileName, title, artist, sectors));
                var result = new RippedTrack(n, path, agreed, passes, copyCrc, verdict);
                ripped.Add(result);
                observer.TrackFinished(result);
                observer.Say($"Track {n}: {status}, {passes} pass(es), {(verdict.IsAccurate ? $"accurate ({verdict.Confidence} rips)" : arBlocks is null ? "not in AccurateRip" : "no AccurateRip match")}");
            }
        }

        var baseName = $"{FileNames.Safe(plan.AlbumArtist)} - {FileNames.Safe(plan.DiscTitle)}";
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".toc"), cdrdao.Text, ct);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".cue"),
            Sidecars.Cue(cdrdao.Toc, disc.Cddb, plan.AlbumArtist, plan.Album, sidecarTracks, Version), ct);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".m3u"), Sidecars.M3u(sidecarTracks), ct);

        var identity = disc.Identity;
        var driveName = identity is null ? disc.Device : $"{identity.Vendor} {identity.Model} (revision {identity.Revision})";
        var logDisc = new LogDisc(driveName, CdDrive.EngineDescription, disc.Offset, cdrdao.Version, false,
            plan.AlbumArtist, plan.DiscTitle, disc.Cddb, disc.DiscId, DiscIds.MusicBrainzAttachUrl(toc), plan.ReleaseId, toc,
            disc.SpeedLimit);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".log"),
            RipLog.Write(logDisc, logTracks, Version, DateTimeOffset.UtcNow), ct);

        return new RipResult(albumDir, ripped);
    }

    /// The release's 500-pixel front (then its release group's), for cover.jpg
    /// and for the Disc screen while he chooses. Null when there is none, or
    /// the archive cannot be reached.
    public static async Task<byte[]?> FrontCoverAsync(ReleasePlan plan, CancellationToken ct = default)
    {
        if (plan.ReleaseId is null) return null;   // nothing to look up for a self-described disc
        try
        {
            using var http = Http(30);
            return await CoverArt.FrontAsync(http, plan.ReleaseId!, plan.ReleaseGroupId, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// The command line: all three steps in a row.
    public static async Task<RipResult> RunAsync(RipOptions options, IRipObserver observer, CancellationToken ct = default)
    {
        var disc = await PrepareAsync(options.Device, options.Offset, observer.Say, speedLimit: options.SpeedLimit, ct: ct);
        var releaseId = options.ReleaseId ?? disc.Candidates.Count switch
        {
            0 => throw new RipException($"Disc {disc.DiscId} is not on any MusicBrainz release. Attach it there, or give a release with --release."),
            1 => disc.Candidates[0].Id,
            _ => throw new ReleaseChoiceNeeded(disc.Candidates),
        };
        using var plan = await PlanAsync(disc, releaseId, options.ConventionsLibrary, ct);
        plan.KeepDisambiguation = options.KeepDisambiguation;
        return await RipAsync(disc, plan, options.Year ?? plan.DefaultYear, options.Library, observer,
            options.MaxRetries, options.MaxPasses, options.AccurateRip, ct: ct);
    }

    private static HttpClient Http(int seconds)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Deadwax/{Version}");
        return http;
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
