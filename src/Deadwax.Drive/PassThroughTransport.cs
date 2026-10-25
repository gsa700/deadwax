namespace Deadwax.Drive;

/// The MMC commands Deadwax's own drive access sends, for the systems that
/// hand a command block to the drive as it is: Linux (SG_IO) and Windows
/// (SPTI). Each system's class says only how a command is run, and how the
/// tray is opened; the command bytes are the same for both, as they were
/// for libcdio's mmc_run_cmd.
public abstract unsafe class PassThroughTransport : IDriveTransport
{
    protected const int SectorBytes = 2352, C2Bytes = 294;

    public string Device { get; }

    protected PassThroughTransport(string device) => Device = device;

    /// One command, reading `length` bytes into `data` (none when 0). True
    /// only when the drive took it, answered without error and delivered
    /// every byte asked for. A drive can end a READ CD early, at a track
    /// start, with a good status: the BDR-209D on Techbench did, and the
    /// missing part stayed zeros that read as clean silence, the same in both
    /// passes, so wrong tracks were called Copy OK (2026-10-24).
    protected abstract bool Run(ReadOnlySpan<byte> cdb, byte* data, int length);

    /// Opens the tray.
    public abstract bool Eject();

    public abstract void Dispose();

    public bool SupportsRawSubchannel => true;

    public byte[]? ReadCd(int lsn, int count, bool rawSubchannel)
    {
        var block = SectorBytes + (rawSubchannel ? 96 : 16);
        var buffer = new byte[count * block];
        Span<byte> cdb = stackalloc byte[12];
        ReadCdCdb(cdb, lsn, count);
        cdb[9] = 0xF8;
        cdb[10] = rawSubchannel ? (byte)0x01 : (byte)0x02;
        for (var attempt = 0; attempt < 6; attempt++)
            fixed (byte* buf = buffer)
                if (Run(cdb, buf, buffer.Length)) return buffer;
        return null;
    }

    public byte[]? ReadAudio(int lsn, int count, bool c2)
    {
        var buffer = new byte[count * (SectorBytes + (c2 ? C2Bytes : 0))];
        Span<byte> cdb = stackalloc byte[12];
        ReadCdCdb(cdb, lsn, count);
        cdb[9] = (byte)(0xF8 | (c2 ? 0x02 : 0x00));   // the whole sector, and with c2 the 294 bytes of C2 error flags
        fixed (byte* buf = buffer) return Run(cdb, buf, buffer.Length) ? buffer : null;
    }

    private static void ReadCdCdb(Span<byte> cdb, int lsn, int count)
    {
        cdb[0] = 0xBE;   // READ CD
        cdb[2] = (byte)(lsn >> 24); cdb[3] = (byte)(lsn >> 16); cdb[4] = (byte)(lsn >> 8); cdb[5] = (byte)lsn;
        cdb[6] = (byte)(count >> 16); cdb[7] = (byte)(count >> 8); cdb[8] = (byte)count;
    }

    public string? ReadCode(bool isrc, byte track)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x42;   // READ SUB-CHANNEL
        cdb[2] = 0x40;
        cdb[3] = isrc ? (byte)0x03 : (byte)0x02;
        cdb[6] = track;
        cdb[8] = 24;
        var data = stackalloc byte[24];
        if (!Run(cdb, data, 24)) return null;
        if ((data[8] & 0x80) == 0) return null;
        var length = isrc ? 12 : 13;
        var code = System.Text.Encoding.ASCII.GetString(data + 9, length).TrimEnd('\0');
        return code.Length == length ? code : null;
    }

    public byte[]? ReadCdTextPacks() => ReadToc(0x05, msf: false) is { } r && r.Length > 4 ? CdTextPacks.FromResponse(r) : null;

    public byte[]? ReadFullToc() => ReadToc(0x02, msf: true);

    /// READ TOC/PMA/ATIP in the given format: its length first, then all of it.
    private byte[]? ReadToc(byte format, bool msf)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x43;
        cdb[1] = msf ? (byte)0x02 : (byte)0x00;
        cdb[2] = format;
        cdb[6] = format == 0x02 ? (byte)1 : (byte)0;   // the full TOC: from session 1
        cdb[8] = 4;
        var head = stackalloc byte[4];
        if (!Run(cdb, head, 4)) return null;
        var length = (head[0] << 8 | head[1]) + 2;
        if (length < 4 || length > 36_864 + 4) return null;
        var buffer = new byte[length];
        cdb[7] = (byte)(length >> 8); cdb[8] = (byte)length;
        fixed (byte* buf = buffer) return Run(cdb, buf, length) ? buffer : null;
    }

    public bool SetSpeed(int? multiple)
    {
        var kbps = multiple is { } m ? Math.Min(65535, m * 176) : 0xFFFF;
        Span<byte> cdb = stackalloc byte[12];
        cdb[0] = 0xBB;   // SET CD SPEED
        cdb[2] = (byte)(kbps >> 8); cdb[3] = (byte)kbps;
        cdb[4] = 0xFF; cdb[5] = 0xFF;   // write speed: the drive's own
        return Run(cdb, null, 0);
    }

    /// INQUIRY: the drive's vendor, model and revision, as libcdio's hwinfo
    /// reads them (bytes 8-15, 16-31, 32-35 of the standard answer).
    public DriveIdentity? Inquiry()
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb[0] = 0x12;
        cdb[4] = 36;
        var data = stackalloc byte[36];
        if (!Run(cdb, data, 36)) return null;
        string Field(int start, int length) => System.Text.Encoding.ASCII.GetString(data + start, length).TrimEnd('\0', ' ');
        var identity = new DriveIdentity(Field(8, 8), Field(16, 16), Field(32, 4));
        return identity.Vendor.Length + identity.Model.Length == 0 ? null : identity;
    }
}
