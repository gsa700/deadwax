using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// Reads what the TOC does not say, straight from the disc through libcdio's
/// MMC pass-through: each track's pregap (where the next track's index 0
/// begins), its ISRC, the disc's catalog number and its CD-Text. This is
/// what `cdrdao read-toc` did for Deadwax until 0.2.16, in two minutes per
/// disc, by scanning every sector's Q subchannel; the same answers come from
/// the sectors that can carry them: a few seconds at the start of each track
/// (ISRC and catalog frames repeat at least every hundred sectors) and the
/// run back from each track's start to where the previous track's audio
/// ends (the pregap). The result is the TOC file cdrdao would have written.
///
/// What is not read this way: index marks above 1 inside a track, which
/// only a full scan finds. No disc in the library has any.
public static class SubchannelReader
{
    /// Sectors per READ CD. 24 x (2352 + 96) stays under 64 KB, the safe
    /// transfer size everywhere.
    public const int SectorsPerCommand = 24;

    /// Sectors read forward from a track's start for its ISRC and the MCN.
    public const int ForwardWindow = 300;

    private const int AudioBytes = 2352;

    public sealed record Frame(int Lsn, QFrame Q);

    /// The subchannel pass over a disc already open in the drive. `toc` is
    /// libcdio's; only its audio session is read (cdrdao reads session 1).
    public static Cdrdao.Result Read(CdDrive drive, Toc toc, Action<string>? say = null, CancellationToken ct = default)
    {
        using var transport = TransportFor(drive);
        return Read(transport, drive, toc, say, ct);
    }

    /// libcdio's pass-through on Linux and Windows; the CD ioctls on macOS.
    public static IMmcTransport TransportFor(CdDrive drive) =>
        OperatingSystem.IsMacOS() ? new MacIoctlTransport(drive.Device) : new LibCdioTransport(drive.NativeHandle);

    public static Cdrdao.Result Read(IMmcTransport io, CdDrive drive, Toc toc, Action<string>? say, CancellationToken ct)
    {
        var tracks = toc.IdTracks;
        var leadout = toc.AudioLeadoutLsn;
        var raw = ChooseMode(io, tracks[0].StartLsn, ct);
        say?.Invoke(raw ? "Subchannel: raw P-W." : "Subchannel: formatted Q.");

        var isrcs = new string?[tracks.Count];
        var pregaps = new int[tracks.Count];
        var mcns = new List<string>();
        pregaps[0] = tracks[0].StartLsn;   // before track 1: whatever the TOC says, never read (cdrdao: SILENCE)

        for (var i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = tracks[i];
            if (!t.IsAudio) continue;
            var start = t.StartLsn;
            var end = i + 1 < tracks.Count ? tracks[i + 1].StartLsn : leadout;

            var frames = ReadFrames(io, raw, start, Math.Min(ForwardWindow, end - start), ct);
            var position = frames.Where(f => f.Q.Trusted && f.Q.Mode == 1).ToList();
            if (position.Count == 0)
                throw new DriveException($"The drive returned no readable subchannel at the start of track {t.Number}.");
            var other = position.FirstOrDefault(f => f.Q.Track != t.Number && !(f.Q.Track == t.Number + 1 && f.Q.Index == 0));
            if (other is not null)
                throw new DriveException($"The subchannel says sector {other.Lsn} belongs to track {other.Q.Track}; the TOC says track {t.Number}. Not ripping.");

            isrcs[i] = frames.FirstOrDefault(f => f.Q.Trusted && f.Q.Mode == 3)?.Q.Isrc;
            mcns.AddRange(frames.Where(f => f.Q.Trusted && f.Q.Mode == 2).Select(f => f.Q.Mcn!));

            if (i + 1 < tracks.Count && tracks[i + 1].IsAudio)
                pregaps[i + 1] = PregapBefore(io, raw, tracks[i + 1].Number, tracks[i + 1].StartLsn, floor: start, ct);
            say?.Invoke($"Track {t.Number}: pregap {CdrdaoToc.Msf(pregaps[i])}, ISRC {isrcs[i] ?? "none"}.");
        }

        // cdrdao's readCatalogScan takes the median of what it saw; READ
        // SUB-CHANNEL is the fallback (the BDR-209D answers it with nothing).
        var mcn = mcns.Count > 0 ? mcns.Order().ElementAt(mcns.Count / 2) : io.ReadCode(isrc: false, 0);
        for (var i = 0; i < tracks.Count; i++)
            if (isrcs[i] is null && tracks[i].IsAudio) isrcs[i] = io.ReadCode(isrc: true, (byte)tracks[i].Number);
        var packs = io.ReadCdTextPacks();
        var (discText, trackTexts) = packs is null ? (null, new Dictionary<int, CdText>()) : CdTextPacks.Decode(packs);

        var list = new List<CdrdaoTrack>(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var index0 = t.StartLsn - pregaps[i];
            var nextIndex0 = i + 1 < tracks.Count ? tracks[i + 1].StartLsn - pregaps[i + 1] : leadout;
            var (pre, copy, four) = drive.TrackFlags(t.Number);
            list.Add(new CdrdaoTrack(t.Number, t.IsAudio, isrcs[i] is { } s ? CdrdaoToc.Isrc(s) : null, pre, copy, four,
                SilenceSectors: i == 0 ? pregaps[0] : 0, PregapSectors: pregaps[i], LengthSectors: nextIndex0 - index0,
                trackTexts.TryGetValue(t.Number, out var text) ? text : null));
        }
        var result = CdrdaoToc.Build(mcn is null ? null : CdrdaoToc.CatalogNumber(mcn), discText, list);
        return new Cdrdao.Result(result.Write(), result, $"Deadwax (libcdio {LibCdio.Version() ?? "unknown"})");
    }

    /// Formatted Q if the drive gives it, else raw P-W, else nothing.
    private static bool ChooseMode(IMmcTransport io, int lsn, CancellationToken ct)
    {
        foreach (var raw in new[] { false, true })
        {
            if (raw && !io.SupportsRawSubchannel) break;
            try
            {
                if (ReadFrames(io, raw, lsn, SectorsPerCommand, ct).Any(f => f.Q.Trusted)) return raw;
            }
            catch (DriveException) { }
        }
        throw new DriveException("The drive returned no readable subchannel data (neither formatted Q nor raw P-W).");
    }

    /// The run back from a track's index 1 to where its index 0 begins, in
    /// sectors; 0 when the previous track runs straight into it. As cdrdao
    /// does, the pregap is measured with the frame's own absolute time.
    private static int PregapBefore(IMmcTransport io, bool raw, int next, int nextStart, int floor, CancellationToken ct)
    {
        var lowestIndex0 = -1;
        var pos = nextStart;
        while (pos > floor)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(75, pos - floor);
            var from = pos - n;
            foreach (var f in ReadFrames(io, raw, from, n, ct).OrderByDescending(f => f.Lsn))
            {
                if (!f.Q.Trusted || f.Q.Mode != 1) continue;
                if (f.Q.Track == next && f.Q.Index == 0) lowestIndex0 = f.Q.AbsoluteLsn;
                else if (f.Q.Track < next) return lowestIndex0 < 0 ? 0 : nextStart - lowestIndex0;
            }
            pos = from;
        }
        return lowestIndex0 < 0 ? 0 : nextStart - lowestIndex0;
    }

    /// `count` sectors from `lsn` with their Q frames, in commands of
    /// SectorsPerCommand (24 x (2352 + 96) stays under 64 KB everywhere).
    public static List<Frame> ReadFrames(IMmcTransport io, bool raw, int lsn, int count, CancellationToken ct)
    {
        var frames = new List<Frame>(count);
        var subBytes = raw ? 96 : 16;
        var block = AudioBytes + subBytes;
        for (var at = lsn; at < lsn + count; at += SectorsPerCommand)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(SectorsPerCommand, lsn + count - at);
            var buffer = io.ReadCd(at, n, raw) ?? throw new DriveException($"READ CD with subchannel failed at sector {at}.");
            for (var s = 0; s < n; s++)
            {
                var sub = buffer.AsSpan(s * block + AudioBytes, subBytes);
                frames.Add(new Frame(at + s, QFrame.Parse(raw ? QFrame.QFromRawPw(sub) : sub)));
            }
        }
        return frames;
    }
}
