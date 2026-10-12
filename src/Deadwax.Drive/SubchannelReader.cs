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
    private const uint TimeoutMs = 30_000;

    public sealed record Frame(int Lsn, QFrame Q);

    /// The subchannel pass over a disc already open in the drive. `toc` is
    /// libcdio's; only its audio session is read (cdrdao reads session 1).
    public static Cdrdao.Result Read(CdDrive drive, Toc toc, Action<string>? say = null, CancellationToken ct = default)
    {
        var tracks = toc.IdTracks;
        var leadout = toc.AudioLeadoutLsn;
        var h = drive.NativeHandle;
        var raw = ChooseMode(h, tracks[0].StartLsn, ct);
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

            var frames = ReadFrames(h, raw, start, Math.Min(ForwardWindow, end - start), ct);
            var position = frames.Where(f => f.Q.Trusted && f.Q.Mode == 1).ToList();
            if (position.Count == 0)
                throw new DriveException($"The drive returned no readable subchannel at the start of track {t.Number}.");
            var other = position.FirstOrDefault(f => f.Q.Track != t.Number && !(f.Q.Track == t.Number + 1 && f.Q.Index == 0));
            if (other is not null)
                throw new DriveException($"The subchannel says sector {other.Lsn} belongs to track {other.Q.Track}; the TOC says track {t.Number}. Not ripping.");

            isrcs[i] = frames.FirstOrDefault(f => f.Q.Trusted && f.Q.Mode == 3)?.Q.Isrc;
            mcns.AddRange(frames.Where(f => f.Q.Trusted && f.Q.Mode == 2).Select(f => f.Q.Mcn!));

            if (i + 1 < tracks.Count && tracks[i + 1].IsAudio)
                pregaps[i + 1] = PregapBefore(h, raw, tracks[i + 1].Number, tracks[i + 1].StartLsn, floor: start, ct);
            say?.Invoke($"Track {t.Number}: pregap {CdrdaoToc.Msf(pregaps[i])}, ISRC {isrcs[i] ?? "none"}.");
        }

        // cdrdao's readCatalogScan takes the median of what it saw.
        var mcn = mcns.Count > 0 ? mcns.Order().ElementAt(mcns.Count / 2) : ReadSubChannelCode(h, 2, 0, 13);
        var (discText, trackTexts) = ReadCdText(h);

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
    private static bool ChooseMode(IntPtr h, int lsn, CancellationToken ct)
    {
        foreach (var raw in new[] { false, true })
        {
            try
            {
                if (ReadFrames(h, raw, lsn, SectorsPerCommand, ct).Any(f => f.Q.Trusted)) return raw;
            }
            catch (DriveException) { }
        }
        throw new DriveException("The drive returned no readable subchannel data (neither formatted Q nor raw P-W).");
    }

    /// The run back from a track's index 1 to where its index 0 begins, in
    /// sectors; 0 when the previous track runs straight into it. As cdrdao
    /// does, the pregap is measured with the frame's own absolute time.
    private static int PregapBefore(IntPtr h, bool raw, int next, int nextStart, int floor, CancellationToken ct)
    {
        var lowestIndex0 = -1;
        var pos = nextStart;
        while (pos > floor)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(75, pos - floor);
            var from = pos - n;
            foreach (var f in ReadFrames(h, raw, from, n, ct).OrderByDescending(f => f.Lsn))
            {
                if (!f.Q.Trusted || f.Q.Mode != 1) continue;
                if (f.Q.Track == next && f.Q.Index == 0) lowestIndex0 = f.Q.AbsoluteLsn;
                else if (f.Q.Track < next) return lowestIndex0 < 0 ? 0 : nextStart - lowestIndex0;
            }
            pos = from;
        }
        return lowestIndex0 < 0 ? 0 : nextStart - lowestIndex0;
    }

    /// READ CD (MMC 6.23) for `count` sectors from `lsn`, audio plus the Q
    /// subchannel: formatted (16 bytes per sector) or raw P-W (96 bytes, Q
    /// taken out of bit 6). Retried as cdrdao does, five times.
    public static unsafe List<Frame> ReadFrames(IntPtr h, bool raw, int lsn, int count, CancellationToken ct)
    {
        var frames = new List<Frame>(count);
        var subBytes = raw ? 96 : 16;
        var block = AudioBytes + subBytes;
        var buffer = new byte[SectorsPerCommand * block];
        for (var at = lsn; at < lsn + count; at += SectorsPerCommand)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(SectorsPerCommand, lsn + count - at);
            var cdb = stackalloc byte[16];
            cdb[0] = 0xBE;
            cdb[2] = (byte)(at >> 24); cdb[3] = (byte)(at >> 16); cdb[4] = (byte)(at >> 8); cdb[5] = (byte)at;
            cdb[6] = (byte)(n >> 16); cdb[7] = (byte)(n >> 8); cdb[8] = (byte)n;
            cdb[9] = 0xF8;
            cdb[10] = raw ? (byte)0x01 : (byte)0x02;
            var rc = -1;
            for (var attempt = 0; attempt < 6 && rc != 0; attempt++)
                fixed (byte* buf = buffer) rc = LibCdio.mmc_run_cmd(h, TimeoutMs, cdb, LibCdio.MmcDataRead, (uint)(n * block), buf);
            if (rc != 0) throw new DriveException($"READ CD with subchannel failed at sector {at} (libcdio {rc}).");
            for (var s = 0; s < n; s++)
            {
                var sub = buffer.AsSpan(s * block + AudioBytes, subBytes);
                frames.Add(new Frame(at + s, QFrame.Parse(raw ? QFrame.QFromRawPw(sub) : sub)));
            }
        }
        return frames;
    }

    /// READ SUB-CHANNEL (MMC 6.28; deprecated since MMC-5, and the BDR-209D
    /// answers it with nothing): parameter 2 = MCN, 3 = ISRC of `track`.
    /// The fallback when the scan saw no such frame. Null when the drive
    /// says there is none.
    public static unsafe string? ReadSubChannelCode(IntPtr h, byte parameter, byte track, int length)
    {
        var cdb = stackalloc byte[16];
        cdb[0] = 0x42;
        cdb[2] = 0x40;          // SubQ
        cdb[3] = parameter;
        cdb[6] = track;
        cdb[8] = 24;
        var data = stackalloc byte[24];
        if (LibCdio.mmc_run_cmd(h, TimeoutMs, cdb, LibCdio.MmcDataRead, 24, data) != 0) return null;
        if ((data[8] & 0x80) == 0) return null;   // MCVal / TCVal: nothing recorded
        var code = System.Text.Encoding.ASCII.GetString(data + 9, length).TrimEnd('\0');
        return code.Length == length ? code : null;
    }

    /// READ TOC/PMA/ATIP format 5 (MMC 6.26.5): the CD-Text packs, asked
    /// for twice, the first time for their length. No CD-Text gives null.
    public static unsafe (CdText? Disc, IReadOnlyDictionary<int, CdText> Tracks) ReadCdText(IntPtr h)
    {
        var none = (Disc: (CdText?)null, Tracks: (IReadOnlyDictionary<int, CdText>)new Dictionary<int, CdText>());
        var cdb = stackalloc byte[16];
        cdb[0] = 0x43;
        cdb[2] = 0x05;
        cdb[7] = 0; cdb[8] = 4;
        var head = stackalloc byte[4];
        if (LibCdio.mmc_run_cmd(h, TimeoutMs, cdb, LibCdio.MmcDataRead, 4, head) != 0) return none;
        var length = (head[0] << 8 | head[1]) + 2;   // the length field counts what follows it
        if (length <= 4 || length > 36_864 + 4) return none;
        var buffer = new byte[length];
        cdb[7] = (byte)(length >> 8); cdb[8] = (byte)length;
        fixed (byte* buf = buffer)
            if (LibCdio.mmc_run_cmd(h, TimeoutMs, cdb, LibCdio.MmcDataRead, (uint)length, buf) != 0) return none;
        var packs = ((buffer[0] << 8 | buffer[1]) - 2) / CdTextPacks.PackBytes * CdTextPacks.PackBytes;
        return packs <= 0 ? none : CdTextPacks.Decode(buffer.AsSpan(4, Math.Min(packs, buffer.Length - 4)));
    }
}
