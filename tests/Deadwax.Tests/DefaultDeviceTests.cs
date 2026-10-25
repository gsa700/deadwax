using Deadwax.Drive;

namespace Deadwax.Tests;

public class DefaultDeviceTests
{
    [Fact]
    public void The_default_device_is_never_empty()
    {
        // A machine with no drive still gets a name to show and to fail on.
        Assert.False(string.IsNullOrWhiteSpace(CdDrive.DefaultDevice));
    }
}
