using System.Buffers.Binary;
using Deadwax.Metadata;

namespace Deadwax.Output;

public sealed class OutputException(string message) : Exception(message);

/// Writes a track as FLAC: CD audio in, a tagged file out.
///
/// libFLAC encodes with its verify mode on (it decodes every frame as it goes
/// and fails on any mismatch) and stores the audio's MD5 in STREAMINFO, which
/// FlacInfo can read back and compare with the MD5 of what was ripped. Tags go
/// on afterwards through TagLibSharp, the library AlbumWall already uses.
/// Pictures are never embedded: art lives in cover.jpg beside the files.
public static class FlacWriter
{
    public const int CompressionLevel = 5; // libFLAC's default, what `flac` uses

    public static unsafe void Encode(string path, ReadOnlySpan<byte> audio)
    {
        if (audio.Length % 4 != 0) throw new ArgumentException("CD audio is whole 16-bit stereo samples.", nameof(audio));
        var frames = audio.Length / 4;

        IntPtr encoder;
        try { encoder = LibFlac.FLAC__stream_encoder_new(); }
        catch (DllNotFoundException) { throw new OutputException("libFLAC is not installed (looked for libFLAC.so.14)."); }
        if (encoder == IntPtr.Zero) throw new OutputException("libFLAC could not create an encoder.");

        try
        {
            LibFlac.FLAC__stream_encoder_set_verify(encoder, 1);
            LibFlac.FLAC__stream_encoder_set_compression_level(encoder, CompressionLevel);
            LibFlac.FLAC__stream_encoder_set_channels(encoder, 2);
            LibFlac.FLAC__stream_encoder_set_bits_per_sample(encoder, 16);
            LibFlac.FLAC__stream_encoder_set_sample_rate(encoder, 44100);
            LibFlac.FLAC__stream_encoder_set_total_samples_estimate(encoder, (ulong)frames);

            var status = LibFlac.FLAC__stream_encoder_init_file(encoder, path, IntPtr.Zero, IntPtr.Zero);
            if (status != LibFlac.InitStatusOk) throw new OutputException($"libFLAC could not start writing {path} (init status {status}).");

            // libFLAC takes each 16-bit sample widened to a 32-bit int.
            const int chunkFrames = 4096;
            var buffer = new int[chunkFrames * 2];
            fixed (int* p = buffer)
            {
                for (var at = 0; at < frames; at += chunkFrames)
                {
                    var n = Math.Min(chunkFrames, frames - at);
                    var src = audio.Slice(at * 4, n * 4);
                    for (var i = 0; i < n * 2; i++) buffer[i] = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2));
                    if (LibFlac.FLAC__stream_encoder_process_interleaved(encoder, p, (uint)n) == 0)
                        throw new OutputException($"libFLAC failed while encoding {path} (state {LibFlac.FLAC__stream_encoder_get_state(encoder)}).");
                }
            }
            if (LibFlac.FLAC__stream_encoder_finish(encoder) == 0)
                throw new OutputException($"libFLAC failed to finish {path} (state {LibFlac.FLAC__stream_encoder_get_state(encoder)}).");
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        finally
        {
            LibFlac.FLAC__stream_encoder_delete(encoder);
        }
    }

    /// Replaces every Vorbis comment with these. Repeated keys become one field
    /// with several values, which is how FLAC stores them anyway (one line each).
    public static void WriteTags(string path, IReadOnlyList<Tag> tags)
    {
        using var file = TagLib.File.Create(path, "audio/flac", TagLib.ReadStyle.None);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, create: true);
        foreach (var key in xiph.ToList()) xiph.RemoveField(key);
        foreach (var group in tags.GroupBy(t => t.Key))
            xiph.SetField(group.Key, group.Select(t => t.Value).ToArray());
        file.RemoveTags(TagLib.TagTypes.Id3v2 | TagLib.TagTypes.Id3v1 | TagLib.TagTypes.Ape);
        file.Save();
    }
}
