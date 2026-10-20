using System.Buffers.Binary;
using System.Text;
using Deadwax.Metadata;

namespace Deadwax.Tests;

/// Deadwax's own FLAC metadata (spec §11 step 1), on small files made here:
/// what the library has none of (an ID3 tag in front, pictures, a blank first
/// value) and the rules it must keep. `deadwax check-flac-metadata` is the
/// comparison with TagLib# over the real library.
public class FlacMetadataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deadwax-flacmeta-").FullName;
    private static readonly byte[] AudioFrames = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray();

    public void Dispose() => Directory.Delete(_dir, true);

    /// "fLaC", STREAMINFO, a Vorbis comment, optional padding, then stand-in audio.
    private string Make(string[] comments, int padding = 0, byte[]? id3 = null, string vendor = "reference libFLAC 1.5.0 20250211")
    {
        var o = new MemoryStream();
        if (id3 is not null) o.Write(id3);
        o.Write("fLaC"u8);
        var streamInfo = Enumerable.Range(0, 34).Select(i => (byte)(i + 1)).ToArray();
        Block(o, 0, streamInfo, last: false);
        var c = new MemoryStream();
        Le(c, vendor.Length); c.Write(Encoding.UTF8.GetBytes(vendor));
        Le(c, comments.Length);
        foreach (var line in comments) { var b = Encoding.UTF8.GetBytes(line); Le(c, b.Length); c.Write(b); }
        Block(o, 4, c.ToArray(), last: padding == 0);
        if (padding > 0) Block(o, 1, new byte[padding], last: true);
        o.Write(AudioFrames);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(path, o.ToArray());
        return path;
    }

    private static void Block(Stream o, int type, byte[] data, bool last)
    {
        o.WriteByte((byte)(type | (last ? 0x80 : 0)));
        o.WriteByte((byte)(data.Length >> 16)); o.WriteByte((byte)(data.Length >> 8)); o.WriteByte((byte)data.Length);
        o.Write(data);
    }

    private static void Le(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }

    private static byte[] Audio(string path) { var all = File.ReadAllBytes(path); return all[^AudioFrames.Length..]; }

    [Fact]
    public void Fields_read_per_key_in_first_appearance_order_keys_upper_cased()
    {
        var path = Make(["title=One", "PERFORMER=A", "ARTIST=X", "performer=B", "NOEQUALS", "Comment="]);
        var fields = FlacMetadata.Read(path).Fields.Select(f => $"{f.Key}={f.Value}");
        // As TagLib# read it: a blank first value is dropped, a line with no '=' skipped.
        Assert.Equal(["TITLE=One", "PERFORMER=A", "PERFORMER=B", "ARTIST=X"], fields);
    }

    [Fact]
    public void A_blank_value_after_the_first_is_kept_as_TagLib_kept_it()
    {
        var path = Make(["PERFORMER=A", "PERFORMER=   "]);
        Assert.Equal(["A", "   "], FlacMetadata.Read(path).Fields.Select(f => f.Value));
    }

    [Fact]
    public void Replace_writes_the_order_it_is_given_and_drops_blanks()
    {
        var path = Make(["OLD=1", "TITLE=x"], padding: 2000);
        OwnFlac.Replace(path, [new("ALBUM", "A"), new("TITLE", "T"), new("PERFORMER", "p1"), new("COMMENT", " "), new("PERFORMER", "p2")]);
        Assert.Equal(["ALBUM=A", "TITLE=T", "PERFORMER=p1", "PERFORMER=p2"], FlacMetadata.Read(path).Fields.Select(f => $"{f.Key}={f.Value}"));
        Assert.Equal(AudioFrames, Audio(path));
    }

    [Fact]
    public void A_change_that_fits_the_padding_rewrites_only_the_metadata()
    {
        var path = Make(["TITLE=x"], padding: 1000);
        var before = new FileInfo(path).Length;
        var offset = FlacMetadata.Read(path).AudioOffset;
        OwnFlac.Update(path, new Dictionary<string, string> { ["REPLAYGAIN_TRACK_GAIN"] = "-6.20 dB" }, []);
        Assert.Equal(before, new FileInfo(path).Length);
        Assert.Equal(offset, FlacMetadata.Read(path).AudioOffset);
        Assert.Equal("-6.20 dB", FlacMetadata.Read(path).First("replaygain_track_gain"));
        Assert.Equal(AudioFrames, Audio(path));
    }

    [Fact]
    public void A_change_that_does_not_fit_grows_the_file_with_4096_bytes_of_padding()
    {
        var path = Make(["TITLE=x"]);
        OwnFlac.Update(path, new Dictionary<string, string> { ["LYRICS"] = new string('l', 300) }, []);
        var layout = FlacMetadata.Read(path).BlockLayout;
        Assert.Equal((1, 4096), layout[^1]);
        Assert.Equal(AudioFrames, Audio(path));
        Assert.False(File.Exists(path + ".deadwax-tmp"));
    }

    [Fact]
    public void Update_keeps_a_key_in_place_and_puts_a_new_one_last()
    {
        var path = Make(["A=1", "B=2", "C=3"], padding: 500);
        OwnFlac.Update(path, new Dictionary<string, string> { ["B"] = "two", ["D"] = "4" }, ["C"]);
        // B was removed and set again, so it goes last too: the order it is given.
        Assert.Equal(["A=1", "B=two", "D=4"], FlacMetadata.Read(path).Fields.Select(f => $"{f.Key}={f.Value}"));
    }

    [Fact]
    public void Pictures_round_trip_and_a_new_front_keeps_the_other_pictures()
    {
        var path = Make(["TITLE=x"], padding: 100);
        var back = new FlacPicture(4, "image/png", "Back ✓", 600, 600, 24, 0, [1, 2, 3]);
        var flac = FlacMetadata.Read(path);
        flac.Pictures.Add(back);
        flac.Save(path);
        OwnFlac.EmbedFront(path, [9, 9, 9, 9]);

        var pictures = FlacMetadata.Read(path).Pictures;
        Assert.Equal(2, pictures.Count);
        Assert.Equal((FlacPicture.FrontCover, "image/jpeg", 0), (pictures[0].Type, pictures[0].MimeType, pictures[0].Width));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, pictures[0].Data);
        Assert.Equal((back.Type, back.MimeType, back.Description, back.Width, back.ColorDepth), (pictures[1].Type, pictures[1].MimeType, pictures[1].Description, pictures[1].Width, pictures[1].ColorDepth));
        Assert.Equal(back.Data, pictures[1].Data);
        Assert.True(CoverEmbed.HasFront(path));
        Assert.Equal(AudioFrames, Audio(path));
    }

    [Fact]
    public void An_ID3v2_tag_in_front_is_read_past_kept_by_an_update_and_dropped_by_replace()
    {
        // A 20-byte ID3v2.4 tag: 10-byte header, size 10 (syncsafe), 10 bytes of frames.
        var id3 = new byte[20];
        "ID3"u8.CopyTo(id3);
        id3[3] = 4; id3[9] = 10;
        var path = Make(["TITLE=x"], padding: 200, id3: id3);
        Assert.Equal("x", FlacMetadata.Read(path).First("TITLE"));

        OwnFlac.Update(path, new Dictionary<string, string> { ["TITLE"] = "y" }, []);
        Assert.Equal("ID3"u8.ToArray(), File.ReadAllBytes(path)[..3]);

        OwnFlac.Replace(path, [new("TITLE", "z")]);
        Assert.Equal("fLaC"u8.ToArray(), File.ReadAllBytes(path)[..4]);
        Assert.Equal("z", FlacMetadata.Read(path).First("TITLE"));
        Assert.Equal(AudioFrames, Audio(path));
    }

    [Fact]
    public void Not_a_FLAC_is_an_IOException_that_callers_already_skip()
    {
        var path = Path.Combine(_dir, "not.flac");
        File.WriteAllText(path, "RIFF....WAVE");
        Assert.IsAssignableFrom<IOException>(Assert.Throws<FlacFormatException>(() => FlacMetadata.Read(path)));
    }

    [Fact]
    public void A_block_that_runs_past_the_end_is_refused()
    {
        var path = Make(["TITLE=x"]);
        var bytes = File.ReadAllBytes(path)[..60];   // cut inside the comment
        File.WriteAllBytes(path, bytes);
        Assert.Throws<FlacFormatException>(() => FlacMetadata.Read(path));
    }
}
