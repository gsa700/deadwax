using System.Text;

namespace Deadwax.Drive;

/// What one sector's Q subchannel says (Red Book, 12 bytes: control/ADR,
/// nine data bytes, CRC-16). Mode 1 is position: track, index, time within
/// the track and absolute time. Mode 2 carries the disc's catalog number
/// (MCN) and mode 3 the track's ISRC, each repeated at least once every
/// hundred sectors. Decoded the way cdrdao's SubChannel classes do it, so the
/// values match whipper's .toc files to the digit.
public sealed record QFrame(
    int Mode, int Control, bool CrcOk,
    int Track, int Index, int Min, int Sec, int Frame, int AMin, int ASec, int AFrame,
    string? Mcn, string? Isrc)
{
    public const int Bytes = 12;

    /// Control nibble bits (MMC 6.26, cdrdao buildToc): 0x01 pre-emphasis,
    /// 0x02 digital copy permitted, 0x04 data track, 0x08 four-channel audio.
    public bool PreEmphasis => (Control & 0x01) != 0;
    public bool CopyPermitted => (Control & 0x02) != 0;
    public bool IsData => (Control & 0x04) != 0;
    public bool FourChannel => (Control & 0x08) != 0;

    /// The absolute position as an LSN (the absolute time counts from the
    /// lead-in, 150 sectors before the program area).
    public int AbsoluteLsn => (AMin * 60 + ASec) * Toc.SectorsPerSecond + AFrame - Toc.LeadIn;

    /// cdrdao's checkConsistency: a frame whose fields cannot be, whatever
    /// its CRC said. A frame that is both CrcOk and Consistent is trusted.
    public bool Consistent => Mode switch
    {
        1 => Track is >= 1 and <= 99 && Index <= 99 && Min <= 99 && Sec <= 59 && Frame <= 74 && AMin <= 99 && ASec <= 59 && AFrame <= 74,
        2 => Mcn is { Length: 13 } m && m.All(char.IsAsciiDigit),
        3 => Isrc is { Length: 12 } i && i.Take(5).All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)) && i.Skip(5).All(char.IsAsciiDigit),
        _ => false,
    };

    public bool Trusted => CrcOk && Consistent;

    /// Decodes 12 Q bytes. The CRC on disc is stored inverted; some drives
    /// hand back formatted Q with the CRC as computed instead (cdrdao's
    /// PQSubChannel16 checks it that way, PWSubChannel96 the inverted way),
    /// so both are accepted.
    public static QFrame Parse(ReadOnlySpan<byte> q)
    {
        if (q.Length < Bytes) throw new ArgumentException("A Q frame is 12 bytes.", nameof(q));
        var crc = Crc16(q[..10]);
        var stored = (ushort)(q[10] << 8 | q[11]);
        var crcOk = stored == crc || stored == (ushort)~crc;
        var mode = q[0] & 0x0F;
        var control = q[0] >> 4;
        return mode switch
        {
            1 => new QFrame(1, control, crcOk, Bcd(q[1]), Bcd(q[2]), Bcd(q[3]), Bcd(q[4]), Bcd(q[5]), Bcd(q[7]), Bcd(q[8]), Bcd(q[9]), null, null),
            2 => new QFrame(2, control, crcOk, 0, 0, 0, 0, 0, 0, 0, Bcd(q[9]), Catalog(q.Slice(1, 7)), null),
            3 => new QFrame(3, control, crcOk, 0, 0, 0, 0, 0, 0, 0, Bcd(q[9]), null, IsrcOf(q.Slice(1, 8))),
            _ => new QFrame(mode, control, crcOk, 0, 0, 0, 0, 0, 0, 0, 0, null, null),
        };
    }

    /// The Q bytes out of a raw 96-byte P-W block: bit 6 of each byte, in order.
    public static byte[] QFromRawPw(ReadOnlySpan<byte> pw)
    {
        if (pw.Length < 96) throw new ArgumentException("A raw P-W block is 96 bytes.", nameof(pw));
        var q = new byte[Bytes];
        for (var i = 0; i < 96; i++)
            if ((pw[i] & 0x40) != 0) q[i / 8] |= (byte)(0x80 >> (i % 8));
        return q;
    }

    /// CRC-16/CCITT (polynomial 0x1021, initial 0) over the first ten bytes,
    /// cdrdao's crctab in bit form.
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// Two BCD digits; a byte that is not BCD comes back as itself, as cdrdao's
    /// bcd2int does, and then fails the consistency check.
    public static int Bcd(byte b) => (b & 0x0F) <= 9 && (b >> 4) <= 9 ? (b >> 4) * 10 + (b & 0x0F) : b;

    /// Thirteen BCD digits in seven bytes (the last nibble is padding).
    private static string Catalog(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(13);
        for (var i = 0; i < 13; i++)
        {
            var nibble = i % 2 == 0 ? b[i / 2] >> 4 : b[i / 2] & 0x0F;
            sb.Append(nibble <= 9 ? (char)('0' + nibble) : '?');
        }
        return sb.ToString();
    }

    /// Five six-bit characters (country and owner), then seven BCD digits (year
    /// and serial), as cdrdao's decodeIsrcCode reads them.
    private static string IsrcOf(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(12);
        sb.Append(SixBit((b[0] >> 2) & 0x3F));
        sb.Append(SixBit(((b[0] & 0x03) << 4) | ((b[1] >> 4) & 0x0F)));
        sb.Append(SixBit(((b[1] & 0x0F) << 2) | ((b[2] >> 6) & 0x03)));
        sb.Append(SixBit(b[2] & 0x3F));
        sb.Append(SixBit((b[3] >> 2) & 0x3F));
        foreach (var (i, high) in new[] { (4, true), (4, false), (5, true), (5, false), (6, true), (6, false), (7, true) })
        {
            var nibble = high ? b[i] >> 4 : b[i] & 0x0F;
            sb.Append(nibble <= 9 ? (char)('0' + nibble) : '?');
        }
        return sb.ToString();
    }

    private static char SixBit(int c) => c <= 9 ? (char)('0' + c) : c is >= 17 and <= 42 ? (char)('A' + (c - 17)) : '?';

    // ---- encoding, for tests and for writing discs one day -----------------

    /// A mode-1 frame's twelve bytes, CRC inverted as on disc.
    public static byte[] EncodePosition(int control, int track, int index, int relativeLsn, int absoluteLsn)
    {
        var q = new byte[Bytes];
        q[0] = (byte)((control << 4) | 1);
        q[1] = ToBcd(track);
        q[2] = ToBcd(index);
        (q[3], q[4], q[5]) = Msf(relativeLsn);
        (q[7], q[8], q[9]) = Msf(absoluteLsn + Toc.LeadIn);
        return WithCrc(q);
    }

    public static byte[] EncodeCatalog(string mcn, int aframe)
    {
        var q = new byte[Bytes];
        q[0] = 0x02;
        for (var i = 0; i < 13; i++)
        {
            var d = mcn[i] - '0';
            if (i % 2 == 0) q[1 + i / 2] |= (byte)(d << 4); else q[1 + i / 2] |= (byte)d;
        }
        q[9] = ToBcd(aframe);
        return WithCrc(q);
    }

    public static byte[] EncodeIsrc(string isrc, int aframe)
    {
        var q = new byte[Bytes];
        q[0] = 0x03;
        var c = isrc.Select(ch => char.IsAsciiDigit(ch) ? ch - '0' : char.ToUpperInvariant(ch) - 'A' + 17).ToArray();
        q[1] = (byte)((c[0] << 2) | (c[1] >> 4));
        q[2] = (byte)(((c[1] & 0x0F) << 4) | (c[2] >> 2));
        q[3] = (byte)(((c[2] & 0x03) << 6) | c[3]);
        q[4] = (byte)(c[4] << 2);
        for (var i = 5; i < 12; i++)
        {
            var d = isrc[i] - '0';
            var at = 5 + (i - 5) / 2;
            if ((i - 5) % 2 == 0) q[at] |= (byte)(d << 4); else q[at] |= (byte)d;
        }
        q[9] = ToBcd(aframe);
        return WithCrc(q);
    }

    private static byte[] WithCrc(byte[] q)
    {
        var crc = (ushort)~Crc16(q.AsSpan(0, 10));
        q[10] = (byte)(crc >> 8);
        q[11] = (byte)crc;
        return q;
    }

    private static (byte, byte, byte) Msf(int sectors) =>
        (ToBcd(sectors / (Toc.SectorsPerSecond * 60)), ToBcd(sectors / Toc.SectorsPerSecond % 60), ToBcd(sectors % Toc.SectorsPerSecond));

    private static byte ToBcd(int d) => d is >= 0 and <= 99 ? (byte)(((d / 10) << 4) | (d % 10)) : (byte)d;
}
