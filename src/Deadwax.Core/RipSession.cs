using System.Text.Json;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;
using Deadwax.Verify;

using System.Reflection;

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
    /// A track whose first read matches AccurateRip with at least this many
    /// rips is read once (spec §11 step 0). Null = read every track twice.
    public int? OneReadConfidence { get; init; } = OneRead.DefaultConfidence;
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

public sealed record PlannedTrack(int Number, string Title, string? Artist, int Sectors, string AlbumArtist)
{
    /// In the track style chosen now (Filing.Current), so the Disc screen's
    /// preview follows a change made in Preferences.
    public string FileName => FileNames.Track(AlbumArtist, Number, Title);
}

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

        EditionYear = root.TryGetProperty("date", out var d) ? LibraryConventions.Year(d.GetString()) : null;
        OriginalYear = rg.ValueKind == JsonValueKind.Object && rg.TryGetProperty("first-release-date", out var f)
            ? LibraryConventions.Year(f.GetString()) : null;
        DefaultYear = LibraryConventions.DefaultYear(root) ?? EditionYear ?? OriginalYear ?? "0000";

        var tracks = new List<PlannedTrack>();
        foreach (var t in disc.Toc.IdTracks.Where(t => t.IsAudio))
        {
            var tags = ReleaseTags.ForTrack(root, medium, t.Number, disc.DiscId, null);
            var title = tags.First(x => x.Key == "TITLE").Value;
            var artist = tags.FirstOrDefault(x => x.Key == "ARTIST")?.Value;
            tracks.Add(new PlannedTrack(t.Number, title, artist, disc.Toc.EndLsn(t) - t.StartLsn, AlbumArtist));
        }
        Tracks = tracks;
    }

    public string AlbumDirectory(string library, string year) =>
        FileNames.AlbumPath(library, AlbumArtist, year, DiscTitle);

    public void Dispose() => Document.Dispose();
}

/// What a rip reports as it goes. All calls come from the ripping thread.
public interface IRipObserver
{
    void Say(string message);
    void TrackStarted(int track) { }
    void Progress(int track, string pass, int done, int total);
    void PassFinished(int track, string pass, ReadReport report) { }
    /// A sector turned Recovered or Skipped, reported while the pass runs.
    void SectorDamaged(int track, string pass, int sector, SectorState state) { }
    /// A damaged sector being read again, try by try, and how it ended
    /// (Deadwax's own reader only; paranoia does not say).
    void Retrying(int track, string pass, RetryStatus status) { }
    void TrackFinished(RippedTrack track) { }
}

/// One rip, in three steps the window can pause between (spec §4):
///   Prepare: TOC, cdrdao, the releases the disc is on;
///   Plan: one release, with names, years and tags;
///   Rip: every track read once and checked with AccurateRip, and read again
///   until two passes agree unless AccurateRip proved the first (OneRead);
///   FLAC, .toc, .cue, .m3u, .log and cover.jpg. It refuses to write into a
///   folder that exists. The command line runs all three in a row (RunAsync).
public sealed class RipSession
{
    /// What the .log's first line says made it. The program's own version,
    /// read from the assembly that is running (the app, or the CLI), without
    /// the source-revision suffix. It was a constant, "0.1", and every log up
    /// to 0.2.11 says so whatever version made it (he noticed, 2026-10-10).
    public static string Version => AppVersion.Current;

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
            // unbalanced disc readable. cdrdao's subchannel pass gets it too.
            if (speedLimit is { } s) drive.LimitSpeed(s);
            identity = drive.ReadIdentity();
            toc = drive.ReadToc();
        }
        var offset = offsetOverride
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
        say($"Disc {discId}, {disc.AudioTracks} audio tracks. Reading ISRCs, CD-Text and pregaps...");
        return await ReadSubchannelAsync(disc, ct);
    }

    /// The pass over the subchannel: ISRCs, catalog, CD-Text and pregaps
    /// (spec §4), read by Deadwax itself since 0.2.16 (SubchannelReader); set
    /// DEADWAX_SUBCHANNEL=cdrdao to have cdrdao read it instead, for
    /// comparing the two. Either way its TOC must agree with libcdio's audio
    /// session, or nothing is ripped.
    public static async Task<PreparedDisc> ReadSubchannelAsync(PreparedDisc disc, CancellationToken ct = default)
    {
        var read = Environment.GetEnvironmentVariable("DEADWAX_SUBCHANNEL") == "cdrdao"
            ? await Cdrdao.ReadTocAsync(disc.Device, disc.SpeedLimit, ct)
            : await Task.Run(() =>
            {
                using var drive = CdDrive.Open(disc.Device);
                return SubchannelReader.Read(drive, disc.Toc, null, ct);
            }, ct);
        var audio = new Toc(disc.Toc.IdTracks, disc.Toc.AudioLeadoutLsn);
        var readToc = read.Toc.ToToc();
        if (!readToc.Tracks.SequenceEqual(audio.Tracks) || readToc.LeadoutLsn != audio.LeadoutLsn)
            throw new RipException("The subchannel and the TOC disagree about where the tracks are; not ripping.");
        return disc with { Cdrdao = read };
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
            // The spelling is read from the artist folders, which the
            // one-level "Artist - Album" style does not have.
            var folders = Filing.Current.Folder == FolderStyle.ArtistDashAlbum
                ? new ArtistFolders()
                : await ArtistFolders.ScanAsync(conventionsLibrary);
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
        int? oneReadConfidence = OneRead.DefaultConfidence, byte[]? cover = null, CancellationToken ct = default)
    {
        var cdrdao = disc.Cdrdao ?? throw new RipException("The disc's subchannel has not been read yet.");
        // The filing choices as they are when the rip starts: a change in
        // Preferences mid-rip must not name half the tracks one way.
        var filing = Filing.Current;
        var root = plan.Root;
        var toc = disc.Toc;
        var albumDir = plan.AlbumDirectory(library, year);
        if (!FileNames.IsInside(library, albumDir))
            throw new RipException($"{albumDir} is not inside the library {library}; the release's names would file it elsewhere. Not ripping.");
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
        // One quick try only: the window has usually fetched it already, and
        // when the archive is down the disc should not wait on it (`deadwax art`
        // fetches it later).
        if (cover is null && plan.ReleaseId is not null)
        {
            observer.Say("Front cover: one quick try at the Cover Art Archive...");
            cover = await FrontCoverAsync(plan, ct, quick: true);
        }
        if (cover is null)
            observer.Say(plan.ReleaseId is null ? "No front cover: the disc is self-described."
                : $"No front cover reachable; ripping without it. Later: deadwax art \"{albumDir}\"");
        else await File.WriteAllBytesAsync(Path.Combine(albumDir, "cover.jpg"), cover, ct);

        var audioTracks = toc.IdTracks.Where(t => t.IsAudio).ToList();
        var ripped = new List<RippedTrack>();
        var logTracks = new List<LogTrack>();
        var sidecarTracks = new List<SidecarTrack>();
        var loudness = new List<(string Path, LoudnessMeter Meter)>();

        // Set again here: the cap is lost if the disc was taken out and put
        // back between the scan and the rip.
        if (disc.SpeedLimit is { } speed)
        {
            CdDrive.LimitSpeed(disc.Device, speed);
            observer.Say($"Drive speed limited to {speed}x.");
        }

        using (var reader = SecureReader.Open(disc.Device))
        {
            reader.NormalSpeed = disc.SpeedLimit;
            foreach (var track in audioTracks)
            {
                ct.ThrowIfCancellationRequested();
                var n = track.Number;
                var sectors = toc.EndLsn(track) - track.StartLsn;
                var cdTrack = cdrdao.Toc.Tracks.First(t => t.Number == n);
                observer.TrackStarted(n);

                // The first read is checked with AccurateRip at once: a match
                // proves it, and the track is done (OneRead, spec §11 step 0).
                // Otherwise, or when every track is to be read twice, read until
                // two passes agree: the first is the test, each later one a copy
                // compared with the pass before it.
                var (audio, report) = reader.ReadTrack(toc, n, disc.Offset, maxRetries, (d, t) => observer.Progress(n, "test", d, t), ct,
                    (s, state) => observer.SectorDamaged(n, "test", s, state), st => observer.Retrying(n, "test", st));
                observer.PassFinished(n, "test", report);
                var testCrc = AudioChecks.Crc32(audio);
                var previousCrc = testCrc;
                uint? copyCrc = null;
                var passes = 1;
                var agreed = false;
                var repairs = report.Repairs;
                var skips = report.Skips;
                var elapsed = report.Elapsed;

                var index = audioTracks.IndexOf(track);
                var isFirst = n == audioTracks[0].Number;
                var isLast = n == audioTracks[^1].Number;
                var (v1, v2) = AccurateRipChecksum.Compute(audio, isFirst, isLast);
                var verdict = AccurateRipMatch.For(arBlocks, index, n, v1, v2);
                var readOnce = oneReadConfidence is { } min && arBlocks is not null && OneRead.Accepts(verdict, report.Skips, min);
                string? edgesReadTwice = null;
                if (readOnce && (isFirst || isLast))
                {
                    // The sectors AccurateRip leaves out, read again on their
                    // own: they must come back the same, byte for byte.
                    var edge = Math.Min(OneRead.EdgeBytes, audio.Length);
                    var edges = new List<(int From, int To, string Name)>();
                    if (isFirst) edges.Add((0, edge, "first"));
                    if (isLast) edges.Add((audio.Length - edge, audio.Length, "last"));
                    foreach (var (from, to, name) in edges)
                    {
                        var (again, edgeReport) = reader.ReadTrackPart(toc, n, disc.Offset, from, to, maxRetries, ct);
                        if (edgeReport.Skips == 0 && again.AsSpan().SequenceEqual(audio.AsSpan(from, to - from))) continue;
                        readOnce = false;
                        observer.Say($"Track {n}: its {name} 5 sectors, which AccurateRip leaves out, read differently the second time; reading the whole track again.");
                        break;
                    }
                    if (readOnce) edgesReadTwice = string.Join(" and ", edges.Select(e => e.Name)) + " 5 sectors";
                }

                string? bestCopy = null;
                if (readOnce) agreed = true;
                else
                {
                    // Every pass is kept until two agree, for "Make best copy" (spec §8)
                    // should none: a vote of them, not whichever came last.
                    var passReads = new List<(byte[] Audio, ReadReport Report)> { (audio, report) };
                    while (passes < maxPasses)
                    {
                        (audio, report) = reader.ReadTrack(toc, n, disc.Offset, maxRetries, (d, t) => observer.Progress(n, "copy", d, t), ct,
                            (s, state) => observer.SectorDamaged(n, "copy", s, state), st => observer.Retrying(n, "copy", st));
                        observer.PassFinished(n, "copy", report);
                        passes++;
                        copyCrc = AudioChecks.Crc32(audio);
                        repairs += report.Repairs;
                        skips += report.Skips;
                        elapsed = report.Elapsed;
                        if (copyCrc == previousCrc && report.Skips == 0) { agreed = true; break; }
                        observer.Say($"Track {n}: pass {passes} differs from pass {passes - 1}; reading again.");
                        previousCrc = copyCrc.Value;
                        passReads.Add((audio, report));
                    }
                    if (agreed) testCrc = copyCrc!.Value;   // the pass it agreed with
                    else if (passReads.Count >= 2)
                    {
                        // Each read's sector state, for where no sample has a majority.
                        var start = ReadWindow.For(toc, track, disc.Offset).TrackStartInWindow;
                        var best = BestCopy.Vote(passReads.Select(x => x.Audio).ToList(),
                            (r, at) => passReads[r].Report.Sectors[Math.Min((start + at) / ReadWindow.SectorBytes, passReads[r].Report.Sectors.Length - 1)]);
                        audio = best.Audio;
                        copyCrc = AudioChecks.Crc32(audio);
                        bestCopy = $"the best copy of the {passReads.Count} reads, not verified: {best.Voted + best.NoMajority} of {best.Sectors} sectors differed between them, " +
                                   $"{best.Voted} decided by a majority, {best.NoMajority} by the best single read";
                        observer.Say($"Track {n}: no two reads alike; kept {bestCopy}.");
                    }
                    (v1, v2) = AccurateRipChecksum.Compute(audio, isFirst, isLast);
                    verdict = AccurateRipMatch.For(arBlocks, index, n, v1, v2);
                    // A best copy AccurateRip confirms is simply right.
                    if (bestCopy is not null && verdict.IsAccurate) { agreed = true; bestCopy = $"the best copy of {passReads.Count} reads, confirmed by AccurateRip ({verdict.Confidence} rips)"; }
                }

                var tags = LibraryConventions.Apply(
                    ReleaseTags.ForTrack(root, plan.Medium, n, disc.DiscId, cdTrack.Isrc ?? cdTrack.Text?.Isrc), year, plan.FolderArtist);
                var title = tags.First(t => t.Key == "TITLE").Value;
                var artist = tags.FirstOrDefault(t => t.Key == "ARTIST")?.Value;
                var fileName = FileNames.Track(filing.Track, plan.AlbumArtist, n, title);
                var path = Path.Combine(albumDir, fileName);

                FlacWriter.Encode(path, audio);
                FlacWriter.WriteTags(path, tags);
                if (FlacInfo.AudioMd5(path) != AudioChecks.Md5(audio))
                    throw new RipException($"{fileName}: the FLAC's stored MD5 does not match the audio read. Not continuing.");
                if (filing.ReplayGain)
                {
                    var meter = new LoudnessMeter();
                    meter.Add(audio);
                    loudness.Add((path, meter));
                }

                var status = agreed ? "Copy OK" : "Copy NOT OK";
                var reads = readOnce
                    ? $"1 ({OneRead.Proof(verdict)}{(edgesReadTwice is null ? "" : $"; the {edgesReadTwice} read twice alike")})"
                    : bestCopy is not null ? $"{passes} (no two reads alike; kept {bestCopy})"
                    : !agreed ? $"{passes} (no two reads alike)"
                    : passes == 2 ? "2 (test and copy CRCs match)" : $"{passes} (the last two reads match)";
                var seconds = sectors / (double)Toc.SectorsPerSecond;
                logTracks.Add(new LogTrack(
                    n, Path.GetRelativePath(library, path), Peak(audio), cdTrack.PreEmphasis,
                    seconds / Math.Max(elapsed.TotalSeconds, 0.001), testCrc, copyCrc, repairs, skips,
                    Ar(arBlocks, index, v1, verdict.V1Confidence), Ar(arBlocks, index, v2, verdict.V2Confidence),
                    status, reads, readOnce));
                sidecarTracks.Add(new SidecarTrack(n, fileName, title, artist, sectors));
                var result = new RippedTrack(n, path, agreed, passes, copyCrc ?? testCrc, verdict);
                ripped.Add(result);
                observer.TrackFinished(result);
                observer.Say($"Track {n}: {status}, {(readOnce ? "one read" : $"{passes} pass(es)")}, {(verdict.IsAccurate ? $"accurate ({verdict.Confidence} rips)" : arBlocks is null ? "not in AccurateRip" : "no AccurateRip match")}");
            }
        }

        // Album gain needs every track, so the tags go on once the last is in.
        if (loudness.Count > 0)
        {
            var meters = loudness.Select(l => l.Meter).ToList();
            var albumLufs = LoudnessMeter.AlbumLufs(meters);
            var albumPeak = meters.Max(m => m.Peak);
            foreach (var (path, meter) in loudness)
                FlacTags.Update(path, LoudnessMeter.Tags(meter, albumLufs, albumPeak), []);
            observer.Say(albumLufs is { } lufs
                ? $"ReplayGain: album {lufs:0.0} LUFS, gain {LoudnessMeter.Gain(lufs, albumPeak):+0.00;-0.00} dB."
                : "ReplayGain: the album is silent; no gain written.");
        }

        var baseName = FileNames.Sidecar(plan.AlbumArtist, plan.DiscTitle);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".toc"), cdrdao.Text, ct);
        if (filing.Cue)
            await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".cue"),
                Sidecars.Cue(cdrdao.Toc, disc.Cddb, plan.AlbumArtist, plan.Album, sidecarTracks, Version), ct);
        if (filing.M3u)
            await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".m3u"), Sidecars.M3u(sidecarTracks), ct);
        if (filing.EmbedCover && CoverEmbed.Apply(albumDir) is var embedded && embedded > 0)
            observer.Say($"Front cover embedded in {embedded} files.");

        var identity = disc.Identity;
        var driveName = identity is null ? disc.Device : $"{identity.Vendor} {identity.Model} (revision {identity.Revision})";
        var logDisc = new LogDisc(driveName, SecureReader.EngineDescription, disc.Offset, cdrdao.Version, false,
            plan.AlbumArtist, plan.DiscTitle, disc.Cddb, disc.DiscId, DiscIds.MusicBrainzAttachUrl(toc), plan.ReleaseId, toc,
            disc.SpeedLimit);
        await File.WriteAllTextAsync(Path.Combine(albumDir, baseName + ".log"),
            RipLog.Write(logDisc, logTracks, Version, DateTimeOffset.UtcNow), ct);

        return new RipResult(albumDir, ripped);
    }

    /// The release's 500-pixel front (then its release group's), for cover.jpg
    /// and for the Disc screen while he chooses. Null when there is none, or
    /// the archive cannot be reached. Patient by default (retries, 30 s each);
    /// `quick` is one try with ten seconds, for when a rip is waiting on it.
    public static async Task<byte[]?> FrontCoverAsync(ReleasePlan plan, CancellationToken ct = default, bool quick = false)
    {
        if (plan.ReleaseId is null) return null;   // nothing to look up for a self-described disc
        try
        {
            using var http = Http(quick ? 10 : 30);
            return await CoverArt.FrontAsync(http, plan.ReleaseId!, plan.ReleaseGroupId, ct, quick ? 1 : CoverArt.DefaultAttempts);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // the last try timed out: an unreachable archive is "no cover", never a failed rip
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
            options.MaxRetries, options.MaxPasses, options.AccurateRip, options.OneReadConfidence, ct: ct);
    }

    private static HttpClient Http(int seconds) => NewHttp(seconds);

    /// An HttpClient with Deadwax's user agent and a timeout in seconds.
    public static HttpClient NewHttp(int seconds)
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
