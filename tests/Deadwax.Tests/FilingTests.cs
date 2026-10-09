using Deadwax.Core;
using Deadwax.Output;

namespace Deadwax.Tests;

/// Preferences > Filing (2026-10-08): every style names things as its label
/// says, the default is his library's convention, and Unbox still recognizes
/// a disc of a set whichever folder style named it.
public class FilingTests
{
    [Theory]
    [InlineData(FolderStyle.ArtistYearAlbum, "Thin Lizzy/1976 - Jailbreak")]
    [InlineData(FolderStyle.ArtistAlbumYear, "Thin Lizzy/Jailbreak (1976)")]
    [InlineData(FolderStyle.ArtistAlbum, "Thin Lizzy/Jailbreak")]
    [InlineData(FolderStyle.ArtistDashAlbum, "Thin Lizzy - Jailbreak")]
    public void Album_folders(FolderStyle style, string expected) =>
        Assert.Equal(Path.Combine("/lib", expected), FileNames.AlbumPath(style, "/lib", "Thin Lizzy", "1976", "Jailbreak"));

    [Theory]
    [InlineData(TrackStyle.ArtistNumberTitle, "AC_DC - 01 - Hells Bells.flac")]
    [InlineData(TrackStyle.NumberDashTitle, "01 - Hells Bells.flac")]
    [InlineData(TrackStyle.NumberTitle, "01 Hells Bells.flac")]
    public void Track_files(TrackStyle style, string expected) =>
        Assert.Equal(expected, FileNames.Track(style, "AC/DC", 1, "Hells Bells"));

    [Fact]
    public void Default_is_his_convention()
    {
        Assert.Equal(FolderStyle.ArtistYearAlbum, Filing.Default.Folder);
        Assert.Equal(TrackStyle.ArtistNumberTitle, Filing.Default.Track);
        Assert.False(Filing.Default.EmbedCover);
        Assert.True(Filing.Default.BackCover && Filing.Default.Cue && Filing.Default.M3u);
    }

    [Theory]
    [InlineData("1989 - The Atlantic Years (1989–1996) (Disc 1 of 5): Skid Row")]
    [InlineData("The Atlantic Years (1989–1996) (Disc 1 of 5): Skid Row (2023)")]
    [InlineData("The Atlantic Years (1989–1996) (Disc 1 of 5): Skid Row")]
    [InlineData("Skid Row - The Atlantic Years (1989–1996) (Disc 1 of 5): Skid Row")]
    public void Unbox_reads_a_set_disc_in_every_style(string folder)
    {
        Assert.True(Unbox.IsSetDisc(Path.Combine("/lib", "Skid Row", folder)));
        Assert.Equal("Skid Row", Unbox.Subtitle(folder));
    }

    // FiledCheck's album-wide rules, on a real ripped album copied aside.
    [Fact]
    public void Filed_check_catches_numbering_and_disagreement()
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Skid Row", "1989 - Skid Row");
        if (!Directory.Exists(source)) return;   // only where his library is
        var dir = Directory.CreateTempSubdirectory("deadwax-filed-").FullName;
        try
        {
            foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            Assert.Empty(FiledCheck.Run(dir, Filing.Default));

            var five = Directory.GetFiles(dir, "*05*.flac").Single();
            Deadwax.Metadata.FlacTags.Update(five, new Dictionary<string, string> { ["TRACKNUMBER"] = "12", ["ALBUM"] = "Something Else" }, []);
            var issues = FiledCheck.Run(dir, Filing.Default);
            Assert.Contains(issues, i => i.StartsWith("track numbers are", StringComparison.Ordinal));
            Assert.Contains(issues, i => i.StartsWith("the tracks disagree on ALBUM", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
