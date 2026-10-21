namespace Deadwax.Output;

/// Bits most significant first, as every field of a FLAC frame is written
/// (RFC 9639 §1.3). Part of Deadwax's own FLAC encoder (spec §11 step 2).
internal sealed class BitWriter
{
    private byte[] _buffer;
    private int _length;
    private ulong _pending;   // bits not yet a whole byte, right-aligned
    private int _pendingBits;

    public BitWriter(int capacity = 1 << 16) => _buffer = new byte[capacity];

    public long BitLength => (long)_length * 8 + _pendingBits;

    /// The low `bits` bits of value, 0 to 32 of them.
    public void Write(uint value, int bits)
    {
        if (bits == 0) return;
        _pending = (_pending << bits) | (value & (bits == 32 ? uint.MaxValue : (1u << bits) - 1));
        _pendingBits += bits;
        while (_pendingBits >= 8)
        {
            _pendingBits -= 8;
            Put((byte)(_pending >> _pendingBits));
        }
        _pending &= (1UL << _pendingBits) - 1;
    }

    /// A two's complement number in `bits` bits.
    public void WriteSigned(long value, int bits) => Write((uint)(value & (bits == 32 ? uint.MaxValue : (1L << bits) - 1)), bits);

    /// `zeros` zero bits, then a one (a Rice quotient, a wasted-bits count).
    public void WriteUnary(ulong zeros)
    {
        while (zeros >= 32) { Write(0, 32); zeros -= 32; }
        Write(1, (int)zeros + 1);
    }

    /// One residual, Rice coded with parameter k (RFC 9639 §9.2.7.3).
    public void WriteRice(int residual, int k)
    {
        var folded = Fold(residual);
        WriteUnary(folded >> k);
        if (k > 0) Write((uint)(folded & ((1UL << k) - 1)), k);
    }

    public static ulong Fold(int r) => r >= 0 ? (ulong)r << 1 : ((ulong)(-(long)r) << 1) - 1;

    public void AlignToByte()
    {
        if (_pendingBits > 0) Write(0, 8 - _pendingBits);
    }

    /// The bytes written so far; only whole bytes (align first).
    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

    private void Put(byte b)
    {
        if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_length++] = b;
    }
}

/// Reading bits back, most significant first, through a 64-bit cache: the
/// decoder reads every residual of every file in the library this way.
internal sealed class BitReader(byte[] data, int start, int end)
{
    private int _next = start;      // the next byte not yet in the cache
    private ulong _cache;           // left-aligned: the top bits are the next to read
    private int _cacheBits;

    /// The byte the next unread bit is in (or the next byte, at a boundary).
    public int BytePosition => _next - _cacheBits / 8;
    public bool AtByteBoundary => (_cacheBits & 7) == 0;

    private void Refill()
    {
        while (_cacheBits <= 56 && _next < end)
        {
            _cache |= (ulong)data[_next++] << (56 - _cacheBits);
            _cacheBits += 8;
        }
    }

    private void Drop(int bits)
    {
        _cache = bits >= 64 ? 0 : _cache << bits;
        _cacheBits -= bits;
    }

    /// 0 to 32 bits as an unsigned number.
    public uint Read(int bits)
    {
        if (bits == 0) return 0;
        if (_cacheBits < bits) Refill();
        if (_cacheBits < bits) throw new FlacDecodeException("The frame ends early.");
        var value = (uint)(_cache >> (64 - bits));
        Drop(bits);
        return value;
    }

    public long ReadSigned(int bits)
    {
        if (bits == 0) return 0;
        // A side channel of 32-bit audio is 33 bits wide.
        var v = bits > 32 ? (long)Read(bits - 32) << 32 | Read(32) : (long)Read(bits);
        return (v & (1L << (bits - 1))) != 0 ? v - (1L << bits) : v;
    }

    public ulong ReadUnary()
    {
        ulong zeros = 0;
        while (true)
        {
            if (_cacheBits == 0) { Refill(); if (_cacheBits == 0) throw new FlacDecodeException("The frame ends early."); }
            var lead = _cache == 0 ? 64 : System.Numerics.BitOperations.LeadingZeroCount(_cache);
            if (lead >= _cacheBits) { zeros += (ulong)_cacheBits; _cache = 0; _cacheBits = 0; continue; }
            zeros += (ulong)lead;
            Drop(lead + 1);
            return zeros;
        }
    }

    public long ReadRice(int k)
    {
        var q = ReadUnary();
        var folded = (q << k) | Read(k);
        return (folded & 1) == 0 ? (long)(folded >> 1) : -(long)(folded >> 1) - 1;
    }

    public void AlignToByte() => Drop(_cacheBits & 7);
}

/// The two checksums of a frame (RFC 9639 §9.1.8 and §9.3): CRC-8 over the
/// header, polynomial x^8 + x^2 + x + 1; CRC-16 over the whole frame,
/// x^16 + x^15 + x^2 + 1. Both start at zero, most significant bit first.
internal static class FlacCrc
{
    private static readonly byte[] Table8 = Build8();
    private static readonly ushort[] Table16 = Build16();

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data) crc = Table8[crc ^ b];
        return crc;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data) crc = (ushort)((crc << 8) ^ Table16[(crc >> 8) ^ b]);
        return crc;
    }

    private static byte[] Build8()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (byte)i;
            for (var j = 0; j < 8; j++) c = (byte)((c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1);
            t[i] = c;
        }
        return t;
    }

    private static ushort[] Build16()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (ushort)(i << 8);
            for (var j = 0; j < 8; j++) c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1);
            t[i] = c;
        }
        return t;
    }
}

public sealed class FlacDecodeException(string message) : IOException(message);
