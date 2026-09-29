using System.Text.Json;
using Deadwax.Drive;
using Deadwax.Metadata;

namespace Deadwax.Tests;

/// A release picked by link when the disc ID isn't attached to it (Coda, the
/// 2015 single CD, whose ID MusicBrainz has on the box sets only): the disc's
/// own track lengths find it on the release, or say why it's the wrong one.
public class ManualReleaseTests
{
    private static JsonDocument Release() => JsonDocument.Parse(Golden.Text("back-in-black.release.json"));
    private static Toc Disc() => CdrdaoToc.Parse(Golden.Text("back-in-black.toc")).ToToc();

    /// Track 10 is 14 s longer on the disc than on MusicBrainz, and this is
    /// the right release: one track that far off must not rule it out.
    [Fact]
    public void The_disc_is_found_on_the_release_by_its_tracks()
    {
        using var release = Release();
        var medium = ReleaseTags.MediumByTracks(release.RootElement, Disc());
        Assert.Equal(1, medium.GetProperty("position").GetInt32());
    }

    [Fact]
    public void A_track_far_from_its_length_rules_the_release_out()
    {
        using var release = Release();
        var disc = Disc();
        var shorter = new Toc(disc.Tracks, disc.LeadoutLsn - 60 * Toc.SectorsPerSecond);
        var e = Assert.Throws<MusicBrainzException>(() => ReleaseTags.MediumByTracks(release.RootElement, shorter));
        Assert.StartsWith($"Track {disc.LastTrack} is ", e.Message);
    }

    [Fact]
    public void Two_tracks_off_rule_the_release_out()
    {
        using var release = Release();
        var disc = Disc();
        // Track 1 ten seconds longer: with track 10's 14 s that is two tracks off.
        var moved = disc.Tracks.Select(t => t.Number == 2 ? t with { StartLsn = t.StartLsn + 10 * Toc.SectorsPerSecond } : t).ToList();
        var e = Assert.Throws<MusicBrainzException>(() => ReleaseTags.MediumByTracks(release.RootElement, new Toc(moved, disc.LeadoutLsn)));
        Assert.StartsWith("Track 1 is ", e.Message);
    }

    [Fact]
    public void Another_track_count_rules_the_release_out()
    {
        using var release = Release();
        var disc = Disc();
        var fewer = new Toc(disc.Tracks.Take(disc.Tracks.Count - 1).ToList(), disc.Tracks[^1].StartLsn);
        var e = Assert.Throws<MusicBrainzException>(() => ReleaseTags.MediumByTracks(release.RootElement, fewer));
        Assert.StartsWith($"This release has no disc with {disc.Tracks.Count - 1} tracks", e.Message);
    }

    [Theory]
    [InlineData("https://musicbrainz.org/release/f728434c-35d2-493a-9d19-e235b000eae9", "f728434c-35d2-493a-9d19-e235b000eae9")]
    [InlineData("  https://musicbrainz.org/release/F728434C-35D2-493A-9D19-E235B000EAE9/discids  ", "f728434c-35d2-493a-9d19-e235b000eae9")]
    [InlineData("f728434c-35d2-493a-9d19-e235b000eae9", "f728434c-35d2-493a-9d19-e235b000eae9")]
    [InlineData("https://musicbrainz.org/release-group/f728434c-35d2-493a-9d19-e235b000eae9", null)]
    [InlineData("Coda", null)]
    public void A_release_link_or_bare_id_is_understood(string text, string? id) =>
        Assert.Equal(id, ReleaseChoice.ReleaseIdFrom(text));
}
