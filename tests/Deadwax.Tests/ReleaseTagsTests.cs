using System.Text.Json;
using Deadwax.Drive;
using Deadwax.Metadata;

namespace Deadwax.Tests;

/// Gate G4, tags: `deadwax check-tags` runs this over the whole library; one
/// real album keeps it in the build.
public class ReleaseTagsTests
{
    [Fact]
    public void Back_in_Black_track_6_is_tagged_exactly_as_the_library_has_it()
    {
        using var release = JsonDocument.Parse(Golden.Text("back-in-black.release.json"));
        var toc = CdrdaoToc.Parse(Golden.Text("back-in-black.toc"));
        const string discId = "ZdiRPrjBeDw8lpYjDkECTqDmkr8-";

        var medium = ReleaseTags.Medium(release.RootElement, discId);
        var built = ReleaseTags.ForTrack(release.RootElement, medium, 6, discId, toc.Tracks[5].Isrc);
        var tags = LibraryConventions.Apply(built, year: "2003", folderArtist: "AC/DC");

        var library = Golden.Text("back-in-black-06.tags")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).Select(kv => new Tag(kv[0], kv[1]));

        Assert.Equal(Sorted(library), Sorted(tags));

        static List<string> Sorted(IEnumerable<Tag> t) => t.Select(x => $"{x.Key}={x.Value}").Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Original_year_replaces_a_reissue_date()
    {
        var tags = LibraryConventions.Apply([new Tag("DATE", "1998"), new Tag("ALBUMARTIST", "Billy Joel")], "1976", null);
        Assert.Equal("1976", tags.Single(t => t.Key == "DATE").Value);
    }

    [Fact]
    public void A_full_date_in_the_chosen_year_is_kept()
    {
        var tags = LibraryConventions.Apply([new Tag("DATE", "2003-02-18")], "2003", null);
        Assert.Equal("2003-02-18", tags.Single(t => t.Key == "DATE").Value);
    }

    [Fact]
    public void The_library_spelling_of_the_artist_wins_but_guest_credits_stay()
    {
        var tags = LibraryConventions.Apply(
            [new Tag("ALBUMARTIST", "Daryl Hall and John Oates"), new Tag("ARTIST", "Daryl Hall and John Oates"),
             new Tag("DATE", "1977")],
            "1977", "Daryl Hall & John Oates");
        Assert.Equal("Daryl Hall & John Oates", tags.Single(t => t.Key == "ALBUMARTIST").Value);
        Assert.Equal("Daryl Hall & John Oates", tags.Single(t => t.Key == "ARTIST").Value);

        var duet = LibraryConventions.Apply(
            [new Tag("ALBUMARTIST", "Elton John"), new Tag("ARTIST", "Elton John & Kiki Dee")], "1976", "Elton John");
        Assert.Equal("Elton John & Kiki Dee", duet.Single(t => t.Key == "ARTIST").Value);
    }
}

public class DiscTitleTests
{
    [Fact]
    public void Folder_title_is_whippers_including_the_disambiguation()
    {
        using var release = JsonDocument.Parse(Golden.Text("original-album-classics-2.release.json"));
        var medium = ReleaseTags.Medium(release.RootElement, discId: null, fallbackPosition: 4);
        // The folder whipper made for this disc on 2026-09-24, same drive, same day.
        Assert.Equal("Original Album Classics (Volume 2) (Disc 4 of 5): Storm Front",
                     ReleaseChoice.DiscTitle(release.RootElement, medium));
    }
}
