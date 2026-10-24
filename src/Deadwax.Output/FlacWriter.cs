using Deadwax.Metadata;

namespace Deadwax.Output;

public sealed class OutputException(string message) : Exception(message);

/// Writes a track as FLAC: CD audio in, a tagged file out.
///
/// Deadwax's own FlacEncoder encodes since 0.6.0 (libFLAC before, until
/// 0.6.2), decodes every frame as it goes with FlacDecoder, and stores
/// the audio's MD5 in STREAMINFO, which FlacInfo can read back and compare
/// with the MD5 of what was ripped. Tags go
/// on afterwards through FlacTags (Deadwax's own FlacMetadata since 0.5.1).
/// Pictures are never embedded: art lives in cover.jpg beside the files.
public static class FlacWriter
{
    /// CD audio to a FLAC file: Deadwax's own encoder (FlacEncoder, every
    /// frame decoded and compared as it is made).
    public static void Encode(string path, ReadOnlySpan<byte> audio)
    {
        try { FlacEncoder.Encode(path, audio, $"Deadwax {Deadwax.Metadata.AppVersion.Current}"); }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// Replaces every Vorbis comment with these. Repeated keys become one field
    /// with several values, which is how FLAC stores them anyway (one line each).
    /// The audio frames after the metadata must come out byte for byte as the
    /// encoder wrote (and decoded) them: the stored MD5 alone would not notice
    /// frames cut short (review 2026-10-24 M2).
    public static void WriteTags(string path, IReadOnlyList<Tag> tags)
    {
        var before = FramesHash(path);
        FlacTags.Replace(path, tags);
        if (!FramesHash(path).AsSpan().SequenceEqual(before))
            throw new OutputException($"{Path.GetFileName(path)}: writing the tags changed the audio frames.");
    }

    /// SHA-256 of everything after the last metadata block.
    private static byte[] FramesHash(string path)
    {
        var offset = FlacMetadata.Read(path).AudioOffset;
        using var file = File.OpenRead(path);
        file.Position = offset;
        return System.Security.Cryptography.SHA256.HashData(file);
    }
}
