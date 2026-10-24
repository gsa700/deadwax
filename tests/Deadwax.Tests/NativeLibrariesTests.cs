using Deadwax.Drive;

namespace Deadwax.Tests;

/// The native libraries are known by a different file name on each platform;
/// the candidate lists are the whole of what makes a build portable, so they
/// are pinned here.
public class NativeLibrariesTests
{
    [Theory]
    [InlineData("cdio")]
    [InlineData("cdio_cdda")]
    [InlineData("cdio_paranoia")]
    public void Every_library_has_a_versioned_name_first_for_this_platform(string name)
    {
        var candidates = NativeLibraries.Candidates(name);
        Assert.NotEmpty(candidates);
        var first = candidates[0];
        if (OperatingSystem.IsWindows()) Assert.EndsWith(".dll", first);
        else if (OperatingSystem.IsMacOS()) Assert.EndsWith(".dylib", first);
        else Assert.Matches(@"\.so\.\d+$", first);
        Assert.Matches(@"\d", first);   // the soname's version, so a -devel package is never needed
        Assert.Equal(first, NativeLibraries.Expected(name));
    }

    [Fact]
    public void An_unknown_library_has_no_candidates()
    {
        Assert.Empty(NativeLibraries.Candidates("nothing"));
        Assert.Equal(IntPtr.Zero, NativeLibraries.Load("nothing"));
    }

    [Fact]
    public void The_default_device_is_never_empty()
    {
        // A machine with no drive still gets a name to show and to fail on.
        Assert.False(string.IsNullOrWhiteSpace(CdDrive.DefaultDevice));
    }
}
