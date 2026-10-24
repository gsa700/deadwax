using System.Buffers.Binary;
using System.Text;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Tests;

/// The 2026-10-24 security review (~/Documents/Security-Review-Deadwax-2026-10-24.md):
/// each test is one of its reproductions, which must now fail to do harm.
public class SecurityReview2Tests
{
    /// Noise is stored VERBATIM, so the last frame's samples land in the file
    /// as they are: "TAG" exactly 128 bytes from the end, where an ID3v1 tag would be.
    private static byte[] NoiseWithTagAtEnd(int frames, bool tag = true)
    {
        var random = new Random(7);
        var left = new short[frames];
        var right = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            right[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }
        if (tag)
        {
            right[frames - 63] = 0x5441;                                          // "TA"
            right[frames - 62] = (short)(0x4700 | (right[frames - 62] & 0xFF));   // "G"
        }
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), left[i]);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), right[i]);
        }
        return bytes;
    }

    [Fact]
    public void M2_writing_tags_never_cuts_audio_that_looks_like_a_trailing_tag()
    {
        var audio = NoiseWithTagAtEnd(10 * 588);
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-m2-{Guid.NewGuid():N}.flac");
        try
        {
            FlacWriter.Encode(path, audio);
            var before = File.ReadAllBytes(path);
            Assert.Equal("TAG", Encoding.ASCII.GetString(before, before.Length - 128, 3));   // the trap is set

            FlacWriter.WriteTags(path, [new Tag("TITLE", "x")]);

            Assert.Equal(audio, FlacDecoder.DecodeToPcm(path, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void M2_a_fake_APE_footer_in_the_audio_is_left_alone()
    {
        var audio = NoiseWithTagAtEnd(10 * 588, tag: false);
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-m2-{Guid.NewGuid():N}.flac");
        try
        {
            FlacWriter.Encode(path, audio);

            // An APE footer claiming 4000 bytes, written over the end of the last frame.
            var bytes = File.ReadAllBytes(path);
            var footer = bytes.AsSpan(bytes.Length - 32, 32);
            "APETAGEX"u8.CopyTo(footer);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(12, 4), 4000);
            File.WriteAllBytes(path, bytes);
            var audioBytes = bytes.Length - FlacMetadata.Read(path).AudioOffset;

            FlacTags.Replace(path, [new Tag("TITLE", "second")]);

            Assert.Equal(audioBytes, new FileInfo(path).Length - FlacMetadata.Read(path).AudioOffset);
        }
        finally { File.Delete(path); }
    }
}
