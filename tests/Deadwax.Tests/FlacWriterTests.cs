using System.Buffers.Binary;
using Deadwax.Metadata;
using Deadwax.Output;
using Deadwax.Verify;

namespace Deadwax.Tests;

public class FlacWriterTests
{
    /// Three seconds that exercise the encoder: a tone on the left, noise on the
    /// right, and full-scale samples at both ends of the range.
    private static byte[] Audio()
    {
        var frames = 3 * 44100;
        var bytes = new byte[frames * 4];
        var random = new Random(1978);
        for (var i = 0; i < frames; i++)
        {
            var left = (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 20000);
            var right = (short)random.Next(short.MinValue, short.MaxValue + 1);
            if (i == 0) (left, right) = (short.MaxValue, short.MinValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), left);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), right);
        }
        return bytes;
    }

    [Fact]
    public void Encoded_file_stores_the_md5_of_the_audio_it_was_given()
    {
        var audio = Audio();
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-{Guid.NewGuid():N}.flac");
        try
        {
            FlacWriter.Encode(path, audio);
            Assert.Equal(AudioChecks.Md5(audio), FlacInfo.AudioMd5(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tags_are_written_exactly_and_repeated_keys_survive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-{Guid.NewGuid():N}.flac");
        try
        {
            FlacWriter.Encode(path, Audio());
            Tag[] tags =
            [
                new("ALBUMARTIST", "AC/DC"), new("TITLE", "Back in Black"), new("DATE", "2003-02-18"),
                new("PERFORMER", "Angus Young"), new("PERFORMER", "Brian Johnson"), new("TITLE_NOTE", "Crücial Crüe – ’"),
            ];
            FlacWriter.WriteTags(path, tags);
            FlacWriter.WriteTags(path, tags); // writing twice replaces, never appends

            using var file = TagLib.File.Create(path);
            var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);
            var readBack = xiph.SelectMany(key => xiph.GetField(key).Select(v => $"{key}={v}")).Order(StringComparer.Ordinal);
            Assert.Equal(tags.Select(t => $"{t.Key}={t.Value}").Order(StringComparer.Ordinal), readBack);
            Assert.Equal(AudioChecks.Md5(Audio()), FlacInfo.AudioMd5(path));   // tagging leaves the audio alone
        }
        finally
        {
            File.Delete(path);
        }
    }
}
