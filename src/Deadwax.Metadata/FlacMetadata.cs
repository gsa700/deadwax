using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Deadwax.Metadata;

/// A FLAC file whose metadata is not what it should be: not a FLAC, cut short,
/// or a block that runs past the end. An IOException, so every caller that
/// skips an unreadable file skips this one too.
public sealed class FlacFormatException(string message) : IOException(message);

/// One PICTURE block (FLAC format, METADATA_BLOCK_PICTURE).
public sealed record FlacPicture(
    int Type, string MimeType, string Description, int Width, int Height, int ColorDepth, int IndexedColors, byte[] Data)
{
    public const int FrontCover = 3;
}

/// The metadata of a FLAC file, read and written by Deadwax itself (spec §11
/// step 1, 2026-10-20), in place of TagLibSharp: the Vorbis comment and the
/// pictures, with every other block kept as it was. The audio is never
/// touched; when the new metadata fits the space the old took (padding
/// included), only that space is rewritten.
///
/// It does what TagLib# 2.3.0 did, so that the library reads and writes the
/// same with either (DEADWAX_TAGS=taglib chose TagLib# for one release):
/// fields are kept per key, in the order each key first appears, keys
/// upper-cased; a key's first value is dropped when blank (TagLib#'s SetField
/// ignores blank text) and later ones kept; only the first non-empty Vorbis
/// comment counts. Saving writes STREAMINFO, the blocks it does not handle in
/// their order, the comment, the pictures, then padding that fills the old
/// space exactly, or 4096 bytes when the new metadata no longer fits: the same
/// blocks of the same sizes in the same places as TagLib#. One difference, on
/// purpose: a key added after others were removed goes last, where TagLib#
/// put it in the slot a removed key left, so Deadwax writes the order it is
/// given (`deadwax check-flac-metadata` compares the two).
public sealed class FlacMetadata
{
    private const int HeaderSize = 4;
    private const int StreamInfo = 0, Padding = 1, VorbisComment = 4, Picture = 6;
    private const int GrowPadding = 4096;

    private readonly List<(int Type, byte[] Data)> _blocks;     // all blocks as read, in order
    private readonly List<(string Key, List<string> Values)> _fields;
    private readonly long _flacStart;                            // where "fLaC" is (after any ID3v2 tag)
    private readonly long _metadataEnd;                          // where the audio frames start

    public string Vendor { get; set; }
    public List<FlacPicture> Pictures { get; }
    /// Where the audio frames start, and STREAMINFO as read: what a change of
    /// metadata must leave as it was (check-flac-metadata compares them).
    public long AudioOffset => _metadataEnd;
    public byte[] StreamInfoBlock => _blocks[0].Data;
    public IReadOnlyList<(int Type, int Size)> BlockLayout => _blocks.Select(b => (b.Type, b.Data.Length)).ToList();

    private FlacMetadata(long flacStart, long metadataEnd, List<(int, byte[])> blocks,
        string vendor, List<(string, List<string>)> fields, List<FlacPicture> pictures)
    {
        _flacStart = flacStart;
        _metadataEnd = metadataEnd;
        _blocks = blocks;
        Vendor = vendor;
        _fields = fields;
        Pictures = pictures;
    }

    /// Every field, in TagLib#'s order: per key, as each key first appeared.
    public IReadOnlyList<KeyValuePair<string, string>> Fields =>
        _fields.SelectMany(f => f.Values.Select(v => new KeyValuePair<string, string>(f.Key, v))).ToList();

    public static FlacMetadata Read(string path)
    {
        using var file = File.OpenRead(path);
        var flacStart = SkipId3v2(file);
        file.Position = flacStart;
        Span<byte> marker = stackalloc byte[4];
        if (file.Read(marker) != 4 || !marker.SequenceEqual("fLaC"u8))
            throw new FlacFormatException($"{path} is not a FLAC file.");

        var blocks = new List<(int, byte[])>();
        Span<byte> header = stackalloc byte[HeaderSize];
        bool last;
        do
        {
            if (file.Read(header) != HeaderSize) throw new FlacFormatException($"{path}: the metadata ends early.");
            last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var size = header[1] << 16 | header[2] << 8 | header[3];
            if (type == 127 || file.Position + size > file.Length) throw new FlacFormatException($"{path}: a metadata block runs past the end.");
            var data = new byte[size];
            file.ReadExactly(data);
            blocks.Add((type, data));
        } while (!last);
        if (blocks.Count == 0 || blocks[0].Item1 != StreamInfo) throw new FlacFormatException($"{path}: STREAMINFO is not the first block.");

        var vendor = "";
        var fields = new List<(string, List<string>)>();
        var comment = blocks.FirstOrDefault(b => b.Item1 == VorbisComment && b.Item2.Length > 0);
        if (comment.Item2 is { } c) (vendor, fields) = ParseComment(c, path);
        var pictures = blocks.Where(b => b.Item1 == Picture && b.Item2.Length > 0).Select(b => ParsePicture(b.Item2, path)).ToList();
        return new FlacMetadata(flacStart, file.Position, blocks, vendor, fields, pictures);
    }

    /// The first value of a key, or null.
    public string? First(string key) =>
        _fields.FirstOrDefault(f => f.Key == key.ToUpperInvariant()).Values?.FirstOrDefault();

    public void Remove(string key) => _fields.RemoveAll(f => f.Key == key.ToUpperInvariant());

    /// A key's values, replacing any it had (TagLib#'s SetField: blank values
    /// are dropped, and a key left with none is removed). A key already
    /// present keeps its place; a new one goes last.
    public void Set(string key, IEnumerable<string> values)
    {
        key = key.ToUpperInvariant();
        var kept = values.Where(v => v is not null && v.Trim().Length != 0).ToList();
        var at = _fields.FindIndex(f => f.Key == key);
        if (kept.Count == 0) { if (at >= 0) _fields.RemoveAt(at); }
        else if (at >= 0) _fields[at] = (key, kept);
        else _fields.Add((key, kept));
    }

    public void Clear() => _fields.Clear();

    /// Writes the metadata back. dropOtherTags removes an ID3v2 tag before the
    /// FLAC data and ID3v1/APE tags after it, as FlacWriter.WriteTags always did.
    public void Save(string path, bool dropOtherTags = false)
    {
        var blocks = new List<(int Type, byte[] Data)> { _blocks[0] };
        blocks.AddRange(_blocks.Skip(1).Where(b => b.Type is not (StreamInfo or VorbisComment or Picture or Padding)));
        blocks.Add((VorbisComment, RenderComment()));
        blocks.AddRange(Pictures.Select(p => (Picture, RenderPicture(p))));

        var oldSpace = _metadataEnd - (_flacStart + 4);
        var used = blocks.Sum(b => (long)HeaderSize + b.Data.Length);
        var padding = oldSpace - HeaderSize - used;
        if (padding < 0) padding = GrowPadding;
        if (padding != 0) blocks.Add((Padding, new byte[padding]));

        var rendered = new MemoryStream();
        for (var i = 0; i < blocks.Count; i++)
        {
            var (type, data) = blocks[i];
            if (data.Length > 0xFFFFFF) throw new FlacFormatException($"{path}: a metadata block is too large for FLAC (over 16 MB).");
            rendered.WriteByte((byte)(type | (i == blocks.Count - 1 ? 0x80 : 0)));
            rendered.WriteByte((byte)(data.Length >> 16));
            rendered.WriteByte((byte)(data.Length >> 8));
            rendered.WriteByte((byte)data.Length);
            rendered.Write(data);
        }

        var (tailStart, tailEnd) = dropOtherTags ? AudioEnd(path) : (-1L, -1L);
        var keepPrefix = !dropOtherTags || _flacStart == 0;
        if (rendered.Length == oldSpace && keepPrefix && tailStart < 0)
        {
            // The same size: only the metadata is rewritten, the audio stays where it is.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
            file.Position = _flacStart + 4;
            rendered.Position = 0;
            rendered.CopyTo(file);
            return;
        }

        // Otherwise the file is written again beside itself and put in place.
        var temp = path + ".deadwax-tmp";
        try
        {
            using (var source = File.OpenRead(path))
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                if (keepPrefix) Copy(source, target, 0, _flacStart);
                target.Write("fLaC"u8);
                rendered.Position = 0;
                rendered.CopyTo(target);
                var audioEnd = tailStart >= 0 ? tailStart : source.Length;
                Copy(source, target, _metadataEnd, audioEnd - _metadataEnd);
                if (tailEnd >= 0 && tailEnd < source.Length) Copy(source, target, tailEnd, source.Length - tailEnd);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private byte[] RenderComment()
    {
        var o = new MemoryStream();
        var vendor = Encoding.UTF8.GetBytes(Vendor);
        WriteLe(o, (uint)vendor.Length);
        o.Write(vendor);
        WriteLe(o, (uint)_fields.Sum(f => f.Values.Count));
        foreach (var (key, values) in _fields)
            foreach (var value in values)
            {
                var line = Encoding.UTF8.GetBytes(key + "=" + value);
                WriteLe(o, (uint)line.Length);
                o.Write(line);
            }
        return o.ToArray();
    }

    private static byte[] RenderPicture(FlacPicture p)
    {
        var o = new MemoryStream();
        var mime = Encoding.Latin1.GetBytes(p.MimeType);
        var description = Encoding.UTF8.GetBytes(p.Description);
        WriteBe(o, (uint)p.Type);
        WriteBe(o, (uint)mime.Length); o.Write(mime);
        WriteBe(o, (uint)description.Length); o.Write(description);
        WriteBe(o, (uint)p.Width);
        WriteBe(o, (uint)p.Height);
        WriteBe(o, (uint)p.ColorDepth);
        WriteBe(o, (uint)p.IndexedColors);
        WriteBe(o, (uint)p.Data.Length); o.Write(p.Data);
        return o.ToArray();
    }

    private static (string, List<(string, List<string>)>) ParseComment(byte[] d, string path)
    {
        var at = 0;
        uint U32()
        {
            if (at + 4 > d.Length) throw new FlacFormatException($"{path}: the Vorbis comment ends early.");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at, 4));
            at += 4;
            return v;
        }
        string Text(uint length)
        {
            if (length > d.Length - at) throw new FlacFormatException($"{path}: a Vorbis comment field runs past the block.");
            var s = Encoding.UTF8.GetString(d, at, (int)length);
            at += (int)length;
            return s;
        }

        var vendor = Text(U32());
        var count = U32();
        var fields = new List<(string Key, List<string> Values)>();
        for (var i = 0; i < count; i++)
        {
            var comment = Text(U32());
            var eq = comment.IndexOf('=');
            if (eq < 0) continue;
            var key = comment[..eq].ToUpper(CultureInfo.InvariantCulture);
            var value = comment[(eq + 1)..];
            var existing = fields.FindIndex(f => f.Key == key);
            if (existing >= 0) fields[existing].Values.Add(value);                 // a later value: kept even when blank
            else if (value.Trim().Length != 0) fields.Add((key, [value]));        // a first value: dropped when blank
        }
        return (vendor, fields);
    }

    private static FlacPicture ParsePicture(byte[] d, string path)
    {
        var at = 0;
        int U32()
        {
            if (at + 4 > d.Length) throw new FlacFormatException($"{path}: a PICTURE block ends early.");
            var v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at, 4));
            at += 4;
            return (int)v;
        }
        byte[] Bytes(int length)
        {
            if (length < 0 || length > d.Length - at) throw new FlacFormatException($"{path}: a PICTURE block runs past its end.");
            var b = d.AsSpan(at, length).ToArray();
            at += length;
            return b;
        }
        var type = U32();
        var mime = Encoding.Latin1.GetString(Bytes(U32()));
        var description = Encoding.UTF8.GetString(Bytes(U32()));
        int width = U32(), height = U32(), depth = U32(), colors = U32();
        return new FlacPicture(type, mime, description, width, height, depth, colors, Bytes(U32()));
    }

    /// Where an ID3v2 tag in front of the FLAC data ends (0 when there is none).
    private static long SkipId3v2(FileStream file)
    {
        Span<byte> h = stackalloc byte[10];
        if (file.Read(h) != 10 || !h[..3].SequenceEqual("ID3"u8)) return 0;
        var size = (h[6] & 0x7F) << 21 | (h[7] & 0x7F) << 14 | (h[8] & 0x7F) << 7 | (h[9] & 0x7F);
        return 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);   // a footer, when the flags say so
    }

    /// Where tags after the audio start and end: an APE tag, then an ID3v1
    /// tag, as TagLib# looks for them. (-1, -1) when there are none.
    private static (long Start, long End) AudioEnd(string path)
    {
        using var file = File.OpenRead(path);
        var end = file.Length;
        var start = end;
        Span<byte> b = stackalloc byte[32];
        if (end >= 128)
        {
            file.Position = end - 128;
            if (file.Read(b[..3]) == 3 && b[..3].SequenceEqual("TAG"u8)) start = end - 128;
        }
        if (start >= 32)
        {
            file.Position = start - 32;
            if (file.Read(b) == 32 && b[..8].SequenceEqual("APETAGEX"u8))
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12, 4));
                var hasHeader = (BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(20, 4)) & 0x80000000) != 0;
                start -= size + (hasHeader ? 32 : 0);
            }
        }
        return start == end ? (-1, -1) : (start, end);
    }

    private static void Copy(Stream source, Stream target, long from, long count)
    {
        source.Position = from;
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new FlacFormatException("The file changed while it was being rewritten.");
            target.Write(buffer, 0, n);
            count -= n;
        }
    }

    private static void WriteLe(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteBe(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
}
