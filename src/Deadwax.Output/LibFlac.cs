using System.Reflection;
using System.Runtime.InteropServices;

namespace Deadwax.Output;

/// The slice of libFLAC's stream encoder Deadwax calls (libFLAC.so.14, FLAC
/// 1.5.0: the same library, and so the same vendor string, as every rip in the
/// library). FLAC 1.4 (libFLAC.so.12, Debian 12 and Ubuntu 24.04) has the same
/// calls; its files are the same audio with that version's vendor string.
/// FLAC__bool is a C int.
internal static unsafe partial class LibFlac
{
    private const string Lib = "FLAC";

    public const int InitStatusOk = 0;

    [LibraryImport(Lib)] public static partial IntPtr FLAC__stream_encoder_new();
    [LibraryImport(Lib)] public static partial void FLAC__stream_encoder_delete(IntPtr encoder);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_set_verify(IntPtr encoder, int value);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_set_compression_level(IntPtr encoder, uint value);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_set_channels(IntPtr encoder, uint value);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_set_bits_per_sample(IntPtr encoder, uint value);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_set_sample_rate(IntPtr encoder, uint value);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_set_total_samples_estimate(IntPtr encoder, ulong value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FLAC__stream_encoder_init_file(IntPtr encoder, string filename, IntPtr progress, IntPtr clientData);

    [LibraryImport(Lib)]
    public static partial int FLAC__stream_encoder_process_interleaved(IntPtr encoder, int* buffer, uint samplesPerChannel);

    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_finish(IntPtr encoder);
    [LibraryImport(Lib)] public static partial int FLAC__stream_encoder_get_state(IntPtr encoder);

    // Like libcdio, only the -devel package installs the bare libFLAC.so.
    // Fedora has FLAC 1.5 (.so.14); Debian and Ubuntu are on 1.4 (.so.12).
    static LibFlac() => NativeLibrary.SetDllImportResolver(typeof(LibFlac).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var candidate in new[] { "libFLAC.so.14", "libFLAC.so.12", "libFLAC.so" })
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
        return IntPtr.Zero;
    }
}
