using Deadwax.Drive;

namespace Deadwax.Tests;

public class PassThroughTransportTests
{
    /// Answers each command with canned bytes, as a drive would, and keeps the
    /// command blocks it was sent.
    private sealed unsafe class FakeDrive(Func<byte[], byte[]?> answer) : PassThroughTransport("fake")
    {
        public readonly List<byte[]> Sent = [];

        protected override bool Run(ReadOnlySpan<byte> cdb, byte* data, int length)
        {
            Sent.Add(cdb.ToArray());
            var reply = answer(cdb.ToArray());
            if (reply is null || reply.Length < length) return false;   // short: a failed command, as Run promises
            reply.AsSpan(0, length).CopyTo(new Span<byte>(data, length));
            return true;
        }

        public override bool Eject() => true;
        public override void Dispose() { }
    }

    [Fact]
    public void Inquiry_reads_vendor_model_and_revision()
    {
        var reply = new byte[36];
        "PIONEER "u8.CopyTo(reply.AsSpan(8));
        "BD-RW   BDR-209D"u8.CopyTo(reply.AsSpan(16));
        "1.51"u8.CopyTo(reply.AsSpan(32));
        var drive = new FakeDrive(_ => reply);
        Assert.Equal(new DriveIdentity("PIONEER", "BD-RW   BDR-209D", "1.51"), drive.Inquiry());
        Assert.Equal([0x12, 0, 0, 0, 36, 0], drive.Sent[0]);
    }

    [Fact]
    public void Read_audio_asks_for_the_whole_sector_and_C2_flags()
    {
        var drive = new FakeDrive(_ => new byte[3 * (2352 + 294)]);
        Assert.NotNull(drive.ReadAudio(0x012345, 3, c2: true));
        var cdb = drive.Sent[0];
        Assert.Equal(0xBE, cdb[0]);
        Assert.Equal([0x00, 0x01, 0x23, 0x45], cdb[2..6]);
        Assert.Equal([0, 0, 3], cdb[6..9]);
        Assert.Equal(0xFA, cdb[9]);
    }

    [Fact]
    public void A_short_answer_is_a_failed_read()
    {
        var drive = new FakeDrive(_ => new byte[2352]);   // one sector where three were asked for
        Assert.Null(drive.ReadAudio(0, 3, c2: false));
    }

    [Theory]
    [InlineData("D:", @"\\.\D:")]
    [InlineData(@"D:\", @"\\.\D:")]
    [InlineData(@"\\.\E:", @"\\.\E:")]
    public void Windows_device_names_become_device_paths(string device, string path) =>
        Assert.Equal(path, WindowsSptiTransport.PathOf(device));
}
