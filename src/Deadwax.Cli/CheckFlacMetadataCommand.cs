using Deadwax.Metadata;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Spec §11 step 1's proof: Deadwax's own FLAC metadata (OwnFlac) against
/// TagLib#'s (TagLibFlac), which it replaces.
///
/// Reads: every FLAC in the library, read both ways; the fields (keys, values,
/// order) and the pictures must be the same. Writes: on copies in a scratch
/// folder, never the library, a sample of files gets each change Deadwax makes
/// (FlacWriter's tags, a ReplayGain-style update, the front cover embedded and
/// embedded again) once each way. After each, both copies must keep STREAMINFO
/// and every byte of audio as they were, hold the same fields (per key, values
/// in order) and pictures, and be laid out the same: the same blocks of the same
/// sizes, the audio at the same place, the same file size. Not the same bytes:
/// TagLib# puts a field it adds after removing others into the dictionary slots
/// they left, last removed first, so its order of keys is an accident of what
/// was there before. Deadwax keeps the order it is given (2026-10-20).
internal static class CheckFlacMetadataCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var writesText = options.Value("--writes");
        var keep = options.Value("--keep");   // a folder: the first copy pair after each step is kept there
        var rest = options.Rest();
        var root = Home(rest.Count > 0 ? rest[0] : "~/Music");
        var writes = writesText is null ? 100 : int.Parse(writesText);
        var flacs = Directory.EnumerateFiles(root, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        string Rel(string p) => Path.GetRelativePath(root, p);
        var failures = 0;

        // ---- reads
        int read = 0, readDiffer = 0, unreadable = 0, withPictures = 0;
        foreach (var flac in flacs)
        {
            IReadOnlyList<KeyValuePair<string, string>> ours, theirs;
            IReadOnlyList<FlacPicture> ourPics, theirPics;
            try { theirs = TagLibFlac.Read(flac); theirPics = TagLibFlac.Pictures(flac); }
            catch (Exception e) when (e is IOException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                unreadable++;
                Console.WriteLine($"SKIP  {Rel(flac)}: TagLib# cannot read it ({e.GetType().Name})");
                continue;
            }
            try { ours = OwnFlac.Read(flac); ourPics = OwnFlac.Pictures(flac); }
            catch (IOException e) { failures++; Console.WriteLine($"FAIL  {Rel(flac)}: ours cannot read what TagLib# reads: {e.Message}"); continue; }
            read++;
            if (theirPics.Count > 0) withPictures++;
            var fieldsSame = ours.SequenceEqual(theirs);
            var picsSame = SamePictures(ours: ourPics, theirs: theirPics);
            if (fieldsSame && picsSame) continue;
            readDiffer++;
            failures++;
            if (readDiffer <= 10)
                Console.WriteLine($"FAIL  {Rel(flac)}: read differs ({(fieldsSame ? "" : "fields ")}{(picsSame ? "" : "pictures")})\n" +
                                  $"      ours:   {Show(ours)}\n      TagLib: {Show(theirs)}");
        }
        Console.WriteLine($"Reads: {read} FLACs read both ways, {read - readDiffer} the same ({withPictures} with pictures), {readDiffer} different, {unreadable} TagLib# could not read.");

        // ---- writes, on copies
        var sample = writes >= flacs.Count ? flacs : Enumerable.Range(0, writes).Select(i => flacs[(int)(i * (flacs.Count / (double)writes))]).ToList();
        var scratch = Path.Combine(Path.GetTempPath(), $"deadwax-check-flac-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var steps = new[] { "tags", "update", "cover", "cover again" };
        var sameLayout = steps.ToDictionary(s => s, _ => 0);
        var sameContent = steps.ToDictionary(s => s, _ => 0);
        var tried = 0;
        try
        {
            foreach (var flac in sample)
            {
                var original = FlacMetadata.Read(flac);
                var audio = await AudioBytesAsync(flac, original.AudioOffset);
                var a = Path.Combine(scratch, "taglib.flac");
                var b = Path.Combine(scratch, "deadwax.flac");
                File.Copy(flac, a, true);
                File.Copy(flac, b, true);
                tried++;

                var tags = TagLibFlac.Read(flac).Select(kv => new Tag(kv.Key, kv.Value)).ToList();
                tags.Add(new Tag("DEADWAX_CHECK", "Crücial Crüe – ’ ✓"));
                tags.Add(new Tag("COMMENT", "   "));          // blank: both must drop it
                var cover = Path.Combine(Path.GetDirectoryName(flac)!, "cover.jpg") is var c && File.Exists(c)
                    ? await File.ReadAllBytesAsync(c) : Enumerable.Range(0, 20000).Select(i => (byte)(i * 7)).ToArray();
                var gain = new Dictionary<string, string> { ["REPLAYGAIN_TRACK_GAIN"] = "-1.23 dB", ["DEADWAX_NEW"] = "x" };
                var ops = new (string Step, Action<string> TagLib, Action<string> Ours)[]
                {
                    ("tags", p => TagLibFlac.Replace(p, tags), p => OwnFlac.Replace(p, tags)),
                    ("update", p => TagLibFlac.Update(p, gain, ["MUSICBRAINZ_ALBUMID"]), p => OwnFlac.Update(p, gain, ["MUSICBRAINZ_ALBUMID"])),
                    ("cover", p => TagLibFlac.EmbedFront(p, cover), p => OwnFlac.EmbedFront(p, cover)),
                    ("cover again", p => TagLibFlac.EmbedFront(p, cover[..^1]), p => OwnFlac.EmbedFront(p, cover[..^1])),
                };
                foreach (var (step, theirs, ours) in ops)
                {
                    theirs(a);
                    ours(b);
                    var problems = new List<string>();
                    var ma = FlacMetadata.Read(a);
                    var mb = FlacMetadata.Read(b);
                    if (!Same(mb.StreamInfoBlock, original.StreamInfoBlock)) problems.Add("STREAMINFO changed");
                    if (!Same(await AudioBytesAsync(b, mb.AudioOffset), audio)) problems.Add("the audio changed");
                    if (!Same(await AudioBytesAsync(a, ma.AudioOffset), audio)) problems.Add("TagLib#'s audio changed");
                    var fieldsA = TagLibFlac.Read(a);
                    var fieldsB = TagLibFlac.Read(b);
                    if (!PerKey(fieldsA).SequenceEqual(PerKey(fieldsB))) problems.Add($"fields differ\n      ours:   {Show(fieldsB)}\n      TagLib: {Show(fieldsA)}");
                    if (!OwnFlac.Read(b).SequenceEqual(fieldsB)) problems.Add("ours reads its own file differently from TagLib#");
                    if (!SamePictures(OwnFlac.Pictures(b), TagLibFlac.Pictures(a))) problems.Add("pictures differ");
                    if (problems.Count > 0)
                    {
                        failures++;
                        Console.WriteLine($"FAIL  {Rel(flac)} after {step}: {string.Join("; ", problems)}");
                        break;
                    }
                    if (keep is not null && tried == 1)
                    {
                        Directory.CreateDirectory(Home(keep));
                        File.Copy(a, Path.Combine(Home(keep), $"taglib-{step.Replace(' ', '-')}.flac"), true);
                        File.Copy(b, Path.Combine(Home(keep), $"deadwax-{step.Replace(' ', '-')}.flac"), true);
                    }
                    sameContent[step]++;
                    if (Layout(a) == Layout(b)) sameLayout[step]++;
                    else
                    {
                        failures++;
                        Console.WriteLine($"FAIL  {Rel(flac)} after {step}: laid out differently\n      ours:   {Layout(b)}\n      TagLib: {Layout(a)}");
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
        Console.WriteLine($"Writes: {tried} copies, each step done both ways:");
        foreach (var s in steps)
            Console.WriteLine($"  {s,-12} same content {sameContent[s]}/{tried}, same layout {sameLayout[s]}/{tried}");
        // ---- fresh files, as a rip makes them: libFLAC's output (no padding),
        // then the rip's own sequence: every tag, ReplayGain, the cover.
        var freshOk = 0;
        var freshDir = Path.Combine(Path.GetTempPath(), $"deadwax-check-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(freshDir);
        try
        {
            var sources = flacs.Count == 0 ? [] : Enumerable.Range(0, 10).Select(i => flacs[i * flacs.Count / 10]).ToList();
            for (var i = 0; i < sources.Count; i++)
            {
                var seconds = 5 + i * 7;
                var pcm = new byte[seconds * 44100 * 4];
                var rng = new Random(i);
                for (var k = 0; k < pcm.Length; k += 2) { var v = (short)(Math.Sin(k * 0.0007 * (i + 1)) * 12000 + rng.Next(-800, 800)); pcm[k] = (byte)v; pcm[k + 1] = (byte)(v >> 8); }
                var a = Path.Combine(freshDir, "taglib.flac");
                var b = Path.Combine(freshDir, "deadwax.flac");
                Deadwax.Output.FlacWriter.Encode(a, pcm);
                File.Copy(a, b, true);
                var tags = TagLibFlac.Read(sources[i]).Select(kv => new Tag(kv.Key, kv.Value)).ToList();
                var gain = new Dictionary<string, string> { ["REPLAYGAIN_TRACK_GAIN"] = "-4.10 dB", ["REPLAYGAIN_TRACK_PEAK"] = "0.912345", ["REPLAYGAIN_ALBUM_GAIN"] = "-3.90 dB", ["REPLAYGAIN_ALBUM_PEAK"] = "0.988000", ["REPLAYGAIN_REFERENCE_LOUDNESS"] = "89.0 dB" };
                var cover = Path.Combine(Path.GetDirectoryName(sources[i])!, "cover.jpg") is var c && File.Exists(c) ? await File.ReadAllBytesAsync(c) : [1, 2, 3];
                var original = FlacMetadata.Read(a);
                var audio = await AudioBytesAsync(a, original.AudioOffset);
                TagLibFlac.Replace(a, tags); OwnFlac.Replace(b, tags);
                TagLibFlac.Update(a, gain, []); OwnFlac.Update(b, gain, []);
                TagLibFlac.EmbedFront(a, cover); OwnFlac.EmbedFront(b, cover);
                var ma = FlacMetadata.Read(a);
                var mb = FlacMetadata.Read(b);
                var ok = PerKey(TagLibFlac.Read(a)).SequenceEqual(PerKey(TagLibFlac.Read(b)))
                         && SamePictures(OwnFlac.Pictures(b), TagLibFlac.Pictures(a))
                         && Same(mb.StreamInfoBlock, original.StreamInfoBlock)
                         && Same(await AudioBytesAsync(b, mb.AudioOffset), audio)
                         && Layout(a) == Layout(b)
                         && Deadwax.Verify.FlacInfo.AudioMd5(b) == Deadwax.Verify.AudioChecks.Md5(pcm);
                if (ok) freshOk++;
                else { failures++; Console.WriteLine($"FAIL  fresh file {i + 1} ({seconds} s, tags from {Rel(sources[i])}): differs\n      ours:   {Layout(b)}\n      TagLib: {Layout(a)}"); }
            }
            Console.WriteLine($"Fresh files: {freshOk}/{sources.Count} encoded by libFLAC and taken through tags, ReplayGain and the cover both ways: same content, layout and audio.");
        }
        finally
        {
            Directory.Delete(freshDir, true);
        }

        Console.WriteLine(failures == 0 ? "PASS: Deadwax's FLAC metadata reads and writes as TagLib# does." : $"FAIL: {failures} problem(s) above.");
        return failures == 0 ? 0 : 1;
    }

    /// The blocks' types and sizes, where the audio starts, and the file's size.
    private static string Layout(string path)
    {
        using var f = File.OpenRead(path);
        var m = FlacMetadata.Read(path);
        return $"{string.Join(",", m.BlockLayout.Select(b => $"{b.Type}:{b.Size}"))} audio@{m.AudioOffset} size {f.Length}";
    }

    private static bool Same(byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y);

    private static async Task<byte[]> AudioBytesAsync(string path, long offset)
    {
        var all = await File.ReadAllBytesAsync(path);
        return all[(int)offset..];
    }

    /// Fields grouped by key, keys sorted: the content, without TagLib#'s
    /// slot-dependent key order after an update.
    private static List<string> PerKey(IReadOnlyList<KeyValuePair<string, string>> fields) =>
        fields.GroupBy(f => f.Key).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + "=" + string.Join("\u0001", g.Select(f => f.Value))).ToList();

    private static bool SamePictures(IReadOnlyList<FlacPicture> ours, IReadOnlyList<FlacPicture> theirs) =>
        ours.Count == theirs.Count && ours.Zip(theirs).All(p =>
            p.First.Type == p.Second.Type && p.First.MimeType == p.Second.MimeType && p.First.Description == p.Second.Description
            && p.First.Width == p.Second.Width && p.First.Height == p.Second.Height && p.First.ColorDepth == p.Second.ColorDepth
            && p.First.IndexedColors == p.Second.IndexedColors && p.First.Data.AsSpan().SequenceEqual(p.Second.Data));

    private static string Show(IReadOnlyList<KeyValuePair<string, string>> fields) =>
        string.Join(" | ", fields.Take(12).Select(f => $"{f.Key}={(f.Value.Length > 30 ? f.Value[..30] + "…" : f.Value)}")) + (fields.Count > 12 ? $" (+{fields.Count - 12})" : "");
}
