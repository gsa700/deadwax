using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Deadwax.Verify;

/// The checksums gate G2 compares, all over a track's raw audio: 16-bit
/// little-endian stereo, exactly the track's TOC length.
public static class AudioChecks
{
    /// whipper's "Test CRC" and "Copy CRC": a plain CRC-32 (zlib's) of the
    /// track's audio. Established 2026-09-24 by decoding 52nd Street's FLACs and
    /// matching all logged values.
    public static uint Crc32(ReadOnlySpan<byte> audio) => System.IO.Hashing.Crc32.HashToUInt32(audio);

    /// What a FLAC file stores in STREAMINFO: the MD5 of its decoded audio. For
    /// 16-bit stereo that is the same bytes as a CD read, so an equal MD5 means
    /// the same audio as the FLAC, whatever encoder or settings made it.
    public static string Md5(ReadOnlySpan<byte> audio) => Convert.ToHexStringLower(MD5.HashData(audio));
}

/// The one thing G2 needs from an existing FLAC: STREAMINFO's audio MD5.
public static class FlacInfo
{
    /// "fLaC", then metadata blocks; STREAMINFO is always first, and its MD5 is
    /// the last 16 of its 34 bytes.
    public static string? AudioMd5(string path)
    {
        Span<byte> head = stackalloc byte[4 + 4 + 34];
        using var file = File.OpenRead(path);
        if (file.Read(head) != head.Length) return null;
        if (!head[..4].SequenceEqual("fLaC"u8) || (head[4] & 0x7F) != 0) return null;
        var md5 = head[(8 + 18)..];
        return md5.IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToHexStringLower(md5);
    }
}

/// Drive read offsets from whipper's config, so Deadwax starts with the offset
/// already measured for each drive (spec §5). Deadwax gets its own config later;
/// this is the seed.
public static class WhipperConfig
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "whipper", "whipper.conf");

    /// The read_offset of the [drive:...] section whose vendor, model and release
    /// match, compared without their padding spaces.
    public static int? ReadOffset(string vendor, string model, string release, string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;

        string? v = null, m = null, r = null;
        int? offset = null;
        int? found = null;
        foreach (var raw in File.ReadLines(path).Append("[end]"))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (v is not null && Same(v, vendor) && Same(m, model) && Same(r, release) && offset is not null)
                    found = offset;
                v = m = r = null;
                offset = null;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "vendor": v = value; break;
                case "model": m = value; break;
                case "release": r = value; break;
                case "read_offset" when int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o): offset = o; break;
            }
        }
        return found;

        static bool Same(string? a, string b) =>
            a is not null && string.Join(' ', a.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                          == string.Join(' ', b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
