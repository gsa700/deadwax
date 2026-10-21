using System.Security.Cryptography;

namespace Deadwax.Output;

/// STREAMINFO (RFC 9639 §8.2): what every FLAC states about itself first.
public sealed record FlacStreamInfo(
    int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize,
    int SampleRate, int Channels, int BitsPerSample, long TotalSamples, byte[] Md5);

/// A FLAC decoder written from RFC 9639 (spec §11 step 2), for Deadwax's own
/// encoder to check every frame it writes, and for checking FLAC files from
/// any encoder: every subframe type (CONSTANT, VERBATIM, FIXED 0-4, LPC
/// 1-32), both Rice codings and escapes, wasted bits, every channel
/// assignment, fixed or variable block sizes. Both CRCs are checked on every
/// frame and the MD5 at the end. It decodes; it does not play.
public static class FlacDecoder
{
    /// The whole file as PCM, interleaved, each sample little-endian in
    /// (bits + 7) / 8 bytes: for CD audio, exactly the bytes ripped. Throws
    /// FlacDecodeException on a bad CRC, a broken frame or an MD5 that does
    /// not match the audio (an MD5 of all zeros means "not given").
    public static byte[] DecodeToPcm(string path, out FlacStreamInfo info)
    {
        var data = File.ReadAllBytes(path);
        info = ReadStreamInfo(data, out var at);
        var bytesPerSample = (info.BitsPerSample + 7) / 8;
        var pcm = new MemoryStream(info.TotalSamples > 0 ? (int)Math.Min(int.MaxValue, info.TotalSamples * info.Channels * bytesPerSample) : 1 << 20);
        var channels = new long[info.Channels][];
        for (var c = 0; c < channels.Length; c++) channels[c] = new long[Math.Max(info.MaxBlockSize, 1)];
        var row = new byte[info.Channels * bytesPerSample];
        long samples = 0;
        while (at < data.Length)
        {
            var used = DecodeFrame(data, at, data.Length, info, ref channels, out var blockSize);
            at += used;
            for (var i = 0; i < blockSize; i++)
            {
                for (var c = 0; c < info.Channels; c++)
                {
                    var v = channels[c][i];
                    for (var b = 0; b < bytesPerSample; b++) row[c * bytesPerSample + b] = (byte)(v >> (8 * b));
                }
                pcm.Write(row);
            }
            samples += blockSize;
        }
        if (info.TotalSamples > 0 && samples != info.TotalSamples)
            throw new FlacDecodeException($"{path}: {samples} samples decoded, STREAMINFO says {info.TotalSamples}.");
        var bytes = pcm.ToArray();
        if (info.Md5.Any(b => b != 0) && !MD5.HashData(bytes).AsSpan().SequenceEqual(info.Md5))
            throw new FlacDecodeException($"{path}: the decoded audio does not match the MD5 in STREAMINFO.");
        return bytes;
    }

    /// STREAMINFO, and where the first frame starts (after the last metadata block).
    public static FlacStreamInfo ReadStreamInfo(byte[] data, out int framesStart)
    {
        var at = 0;
        if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            at = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F)) + ((data[5] & 0x10) != 0 ? 10 : 0);
        if (data.Length < at + 4 + 4 + 34 || data[at] != 'f' || data[at + 1] != 'L' || data[at + 2] != 'a' || data[at + 3] != 'C')
            throw new FlacDecodeException("Not a FLAC stream.");
        at += 4;
        FlacStreamInfo? info = null;
        bool last;
        do
        {
            if (at + 4 > data.Length) throw new FlacDecodeException("The metadata ends early.");
            last = (data[at] & 0x80) != 0;
            var type = data[at] & 0x7F;
            var size = data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];
            at += 4;
            if (at + size > data.Length) throw new FlacDecodeException("A metadata block runs past the end.");
            if (type == 0)
            {
                var r = new BitReader(data, at, at + size);
                info = new FlacStreamInfo(
                    (int)r.Read(16), (int)r.Read(16), (int)r.Read(24), (int)r.Read(24),
                    (int)r.Read(20), (int)r.Read(3) + 1, (int)r.Read(5) + 1,
                    (long)r.Read(4) << 32 | r.Read(32), data.AsSpan(at + 18, 16).ToArray());
            }
            at += size;
        } while (!last);
        framesStart = at;
        return info ?? throw new FlacDecodeException("No STREAMINFO.");
    }

    /// One frame at data[start..]: its samples into channels[c][0..blockSize),
    /// growing them when a frame is longer. Returns the frame's length in bytes.
    internal static int DecodeFrame(byte[] data, int start, int end, FlacStreamInfo? info, ref long[][] channels, out int blockSize)
    {
        var r = new BitReader(data, start, end);
        if (r.Read(14) != 0b11111111111110 || r.Read(1) != 0) throw new FlacDecodeException($"No frame sync at byte {start}.");
        r.Read(1);   // blocking strategy: fixed or variable, it reads the same
        var sizeCode = (int)r.Read(4);
        var rateCode = (int)r.Read(4);
        var channelCode = (int)r.Read(4);
        var depthCode = (int)r.Read(3);
        if (r.Read(1) != 0) throw new FlacDecodeException("A reserved frame-header bit is set.");

        // The frame or sample number, coded as UTF-8 extends it (up to 7 bytes).
        var first = r.Read(8);
        var extra = first < 0x80 ? 0 : first < 0xC0 ? -1 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : first == 0xFE ? 6 : -1;
        if (extra < 0) throw new FlacDecodeException("A malformed frame number.");
        for (var i = 0; i < extra; i++) if ((r.Read(8) & 0xC0) != 0x80) throw new FlacDecodeException("A malformed frame number.");

        blockSize = sizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (sizeCode - 2),
            6 => (int)r.Read(8) + 1,
            7 => (int)r.Read(16) + 1,
            >= 8 => 256 << (sizeCode - 8),
            _ => throw new FlacDecodeException("A reserved block size code."),
        };
        switch (rateCode)
        {
            case 12: r.Read(8); break;
            case 13 or 14: r.Read(16); break;
            case 15: throw new FlacDecodeException("A forbidden sample rate code.");
        }
        var bits = depthCode switch
        {
            0 => info?.BitsPerSample ?? throw new FlacDecodeException("The sample size is in STREAMINFO, which is missing."),
            1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32,
            _ => throw new FlacDecodeException("A reserved sample size code."),
        };
        var headerEnd = r.BytePosition;
        if (r.Read(8) != FlacCrc.Crc8(data.AsSpan(start, headerEnd - start))) throw new FlacDecodeException($"Frame header CRC-8 mismatch at byte {start}.");

        var count = channelCode < 8 ? channelCode + 1 : channelCode <= 10 ? 2 : throw new FlacDecodeException("A reserved channel assignment.");
        if (channels.Length < count) Array.Resize(ref channels, count);
        for (var c = 0; c < count; c++)
            if (channels[c] is null || channels[c].Length < blockSize) channels[c] = new long[blockSize];

        for (var c = 0; c < count; c++)
        {
            // The side channel carries one bit more (RFC 9639 §9.2).
            var side = (channelCode == 8 && c == 1) || (channelCode == 9 && c == 0) || (channelCode == 10 && c == 1);
            Subframe(r, channels[c], blockSize, bits + (side ? 1 : 0));
        }

        switch (channelCode)
        {
            case 8:   // left, side
                for (var i = 0; i < blockSize; i++) channels[1][i] = channels[0][i] - channels[1][i];
                break;
            case 9:   // side, right
                for (var i = 0; i < blockSize; i++) channels[0][i] += channels[1][i];
                break;
            case 10:  // mid, side
                for (var i = 0; i < blockSize; i++)
                {
                    var mid = (channels[0][i] << 1) | (channels[1][i] & 1);
                    var s = channels[1][i];
                    channels[0][i] = (mid + s) >> 1;
                    channels[1][i] = (mid - s) >> 1;
                }
                break;
        }

        r.AlignToByte();
        var crcAt = r.BytePosition;
        var crc = r.Read(16);
        if (crc != FlacCrc.Crc16(data.AsSpan(start, crcAt - start))) throw new FlacDecodeException($"Frame CRC-16 mismatch at byte {start}.");
        return r.BytePosition - start;
    }

    private static void Subframe(BitReader r, long[] s, int n, int bits)
    {
        if (r.Read(1) != 0) throw new FlacDecodeException("A subframe's first bit is not zero.");
        var type = (int)r.Read(6);
        var wasted = r.Read(1) == 1 ? (int)r.ReadUnary() + 1 : 0;
        bits -= wasted;
        if (bits <= 0) throw new FlacDecodeException("More wasted bits than the sample has.");

        if (type == 0)
        {
            var v = r.ReadSigned(bits);
            Array.Fill(s, v, 0, n);
        }
        else if (type == 1)
        {
            for (var i = 0; i < n; i++) s[i] = r.ReadSigned(bits);
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            if (order > n) throw new FlacDecodeException("A predictor order longer than the block.");
            for (var i = 0; i < order; i++) s[i] = r.ReadSigned(bits);
            Residual(r, s, n, order);
            for (var i = order; i < n; i++)
                s[i] += order switch
                {
                    0 => 0,
                    1 => s[i - 1],
                    2 => 2 * s[i - 1] - s[i - 2],
                    3 => 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3],
                    _ => 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4],
                };
        }
        else if (type >= 32)
        {
            var order = type - 31;
            if (order > n) throw new FlacDecodeException("A predictor order longer than the block.");
            for (var i = 0; i < order; i++) s[i] = r.ReadSigned(bits);
            var precision = (int)r.Read(4) + 1;
            if (precision == 16) throw new FlacDecodeException("An invalid coefficient precision.");
            var shift = (int)r.ReadSigned(5);
            if (shift < 0) throw new FlacDecodeException("A negative predictor shift.");
            var coef = new long[order];
            for (var j = 0; j < order; j++) coef[j] = r.ReadSigned(precision);
            Residual(r, s, n, order);
            for (var i = order; i < n; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) sum += coef[j] * s[i - 1 - j];
                s[i] += sum >> shift;
            }
        }
        else throw new FlacDecodeException($"A reserved subframe type ({type}).");

        if (wasted > 0)
            for (var i = 0; i < n; i++) s[i] <<= wasted;
    }

    /// The residual into s[order..n), to which the prediction is added after.
    private static void Residual(BitReader r, long[] s, int n, int order)
    {
        var method = (int)r.Read(2);
        if (method > 1) throw new FlacDecodeException("A reserved residual coding method.");
        var paramBits = method == 0 ? 4 : 5;
        var escape = (1 << paramBits) - 1;
        var partitionOrder = (int)r.Read(4);
        var parts = 1 << partitionOrder;
        if ((n >> partitionOrder) < order || (n & (parts - 1)) != 0 && partitionOrder > 0)
            throw new FlacDecodeException("A partition order the block cannot have.");
        var i = order;
        for (var p = 0; p < parts; p++)
        {
            var count = (n >> partitionOrder) - (p == 0 ? order : 0);
            var k = (int)r.Read(paramBits);
            if (k == escape)
            {
                var raw = (int)r.Read(5);
                for (var j = 0; j < count; j++) s[i++] = r.ReadSigned(raw);
            }
            else
                for (var j = 0; j < count; j++) s[i++] = r.ReadRice(k);
        }
    }
}
