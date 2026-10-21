using System.Buffers.Binary;
using Deadwax.Metadata;

namespace Deadwax.Output;

public sealed class OutputException(string message) : Exception(message);

/// Writes a track as FLAC: CD audio in, a tagged file out.
///
/// Deadwax's own FlacEncoder encodes since 0.6.0 (libFLAC before; it decoded
/// every frame as it went, as FlacEncoder does with FlacDecoder) and stores
/// the audio's MD5 in STREAMINFO, which FlacInfo can read back and compare
/// with the MD5 of what was ripped. Tags go
/// on afterwards through FlacTags (Deadwax's own FlacMetadata since 0.5.1).
/// Pictures are never embedded: art lives in cover.jpg beside the files.
public static class FlacWriter
{
    /// True if libFLAC can be loaded here; otherwise `expected` names the file looked for.
    public static bool IsAvailable(out string expected) => LibFlac.IsAvailable(out expected);

    public const int CompressionLevel = 5; // libFLAC's default, what `flac` uses

    /// DEADWAX_FLAC=libflac: libFLAC encodes instead of FlacEncoder, as before
    /// 0.6.0, for comparing the two (spec §11 step 2). libFLAC goes in the
    /// release after.
    public static bool UseLibFlac => Environment.GetEnvironmentVariable("DEADWAX_FLAC") == "libflac";

    /// CD audio to a FLAC file: Deadwax's own encoder (FlacEncoder, every
    /// frame decoded and compared as it is made), or libFLAC when asked.
    public static void Encode(string path, ReadOnlySpan<byte> audio)
    {
        if (UseLibFlac) { EncodeWithLibFlac(path, audio); return; }
        try { FlacEncoder.Encode(path, audio, $"Deadwax {Deadwax.Metadata.AppVersion.Current}"); }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public static unsafe void EncodeWithLibFlac(string path, ReadOnlySpan<byte> audio)
    {
        if (audio.Length % 4 != 0) throw new ArgumentException("CD audio is whole 16-bit stereo samples.", nameof(audio));
        var frames = audio.Length / 4;

        IntPtr encoder;
        try { encoder = LibFlac.FLAC__stream_encoder_new(); }
        catch (DllNotFoundException) { throw new OutputException($"libFLAC is not installed (looked for {Deadwax.Drive.NativeLibraries.Expected("FLAC")})."); }
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
    public static void WriteTags(string path, IReadOnlyList<Tag> tags) => FlacTags.Replace(path, tags);
}
