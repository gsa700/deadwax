using Deadwax.Verify;

namespace Deadwax.Tests;

public class DriveOffsetsTests
{
    [Fact]
    public void ReadsTheDriveByItsSingleSpacedName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-drives-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"PIONEER BD-RW  BDR-209D 1.10\": 667, \"Other\": 6}");
        try
        {
            Assert.Equal(667, DriveOffsets.ReadOffset("PIONEER ", "BD-RW   BDR-209D", "1.10", path));
            Assert.Null(DriveOffsets.ReadOffset("PIONEER", "BD-RW BDR-209D", "1.11", path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoFileOrBadJsonIsNull()
    {
        Assert.Null(DriveOffsets.ReadOffset("A", "B", "C", "/nonexistent/drives.json"));
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-drives-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "not json");
        try { Assert.Null(DriveOffsets.ReadOffset("A", "B", "C", path)); }
        finally { File.Delete(path); }
    }
}
