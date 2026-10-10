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

