using Deadwax.Verify;

namespace Deadwax.Tests;

/// Real files from the library, copied into Golden/ so the tests need neither a
/// disc, ~/Music, nor the network.
internal static class Golden
{
    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    public static WhipperLog Log(string name) => WhipperLog.Load(PathOf(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));
}
