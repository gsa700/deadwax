using Deadwax.Drive;

namespace Deadwax.Cli;

/// Where does this drive set C2 flags on a clean disc, and are they true?
/// Around each sector given (track starts), reads five shapes three times
/// (a run ending there, one crossing it, one starting there, single sectors
/// either side, and a control run well inside the track), with a read far
/// away between, and for every flagged sector compares its audio with a clean
/// single read of it. Development only; macOS only.
internal static class ProbeC2Command
{
    private const int S = 2352, C2 = 294, Per = S + C2;

    public static Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) return Task.FromResult(Commands.Fail("probe-c2 is macOS only."));
        var device = CdDrive.DefaultDevice;
        var around = args.Select(int.Parse).ToList();
        using var mac = new MacIoctlTransport(device);
        var away = 1000;

        byte[]? Read(int lsn, int count)
        {
            _ = mac.ReadAudio(lsn + 3000 < 190000 ? lsn + 3000 : lsn - 3000, 1, false);   // out of the cache
            return mac.ReadAudio(lsn, count, c2: true);
        }
        byte[] Clean(int lsn)
        {
            for (var i = 0; i < 6; i++)
                if (Read(lsn, 1) is { } r && r.AsSpan(S, C2).IndexOfAnyExcept((byte)0) < 0) return r.AsSpan(0, S).ToArray();
            return [];
        }

        foreach (var b in around)
        {
            Console.WriteLine($"== around sector {b}");
            var shapes = new (string Name, int From, int Count)[]
            {
                ("ending at", b - 24, 24), ("crossing", b - 12, 24), ("starting at", b, 24), ("control, inside", b - away, 24),
            };
            var cleanCache = new Dictionary<int, byte[]>();
            byte[] CleanOf(int lsn) => cleanCache.TryGetValue(lsn, out var c) ? c : cleanCache[lsn] = Clean(lsn);
            foreach (var (name, from, count) in shapes)
                for (var rep = 1; rep <= 3; rep++)
                {
                    var r = Read(from, count);
                    if (r is null) { Console.WriteLine($"  {name,-16} #{rep}: READ FAILED"); continue; }
                    var flagged = Enumerable.Range(0, count).Where(i => r.AsSpan(i * Per + S, C2).IndexOfAnyExcept((byte)0) >= 0).ToList();
                    var wrong = flagged.Count(i => !r.AsSpan(i * Per, S).SequenceEqual(CleanOf(from + i)));
                    Console.WriteLine($"  {name,-16} #{rep}: {flagged.Count,2} flagged" +
                        (flagged.Count == 0 ? "" : $" ({string.Join(",", flagged.Select(i => from + i - b))} from the start), audio actually different in {wrong}"));
                }
            // As a rip reads: streamed at full speed, 24 at a time, up to the
            // track start (split there), then on into the next track.
            for (var rep = 1; rep <= 3; rep++)
            {
                _ = mac.ReadAudio(b - 2400 - 3000, 1, false);
                var flagged = new List<(int Lsn, byte[] Audio, int Bits)>();
                for (var at = b - 2400; at < b + 240;)
                {
                    var n = Math.Min(24, (at < b ? b : b + 240) - at);
                    var r = mac.ReadAudio(at, n, c2: true);
                    if (r is null) { Console.WriteLine($"  streamed #{rep}: READ FAILED at {at - b}"); break; }
                    for (var i = 0; i < n; i++)
                    {
                        var flags = r.AsSpan(i * Per + S, C2);
                        if (flags.IndexOfAnyExcept((byte)0) < 0) continue;
                        var bits = 0; foreach (var x in flags) bits += System.Numerics.BitOperations.PopCount(x);
                        flagged.Add((at + i - b, r.AsSpan(i * Per, S).ToArray(), bits));
                    }
                    at += n;
                }
                var checkedWrong = flagged.Select(f => (f.Lsn, Wrong: !f.Audio.AsSpan().SequenceEqual(CleanOf(b + f.Lsn)), f.Bits)).ToList();
                Console.WriteLine($"  {"streamed",-16} #{rep}: {flagged.Count,2} flagged" + (flagged.Count == 0 ? "" :
                    $" at {string.Join(",", checkedWrong.Select(f => $"{f.Lsn}({f.Bits}b{(f.Wrong ? ", WRONG" : "")})"))}"));
            }
            for (var rep = 1; rep <= 3; rep++)
            {
                var flagged = new List<int>();
                for (var lsn = b - 5; lsn < b + 5; lsn++)
                    if (Read(lsn, 1) is { } r && r.AsSpan(S, C2).IndexOfAnyExcept((byte)0) >= 0) flagged.Add(lsn - b);
                Console.WriteLine($"  {"single sectors",-16} #{rep}: {flagged.Count,2} flagged{(flagged.Count == 0 ? "" : $" ({string.Join(",", flagged)})")}");
            }
        }
        return Task.FromResult(0);
    }
}
