using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Core;

/// Artwork for an album already in the library: cover.jpg from the Cover Art
/// Archive by the release ids in the album's own tags, then back.jpg. For a rip that went in without art because the archive was
/// down at the time (2026-10-06), and for any folder that lost its cover.
public static class AlbumArt
{
    public sealed record Result(bool Front, bool Back, IReadOnlyList<string> Notes);

    /// Fetches what is missing; with replace, fetches both again. Never
    /// touches the audio files.
    public static async Task<Result> FetchAsync(string albumDir, bool replace, Action<string> say, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var coverPath = Path.Combine(albumDir, "cover.jpg");
        var flac = Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal).FirstOrDefault()
                   ?? throw new RipException($"No FLAC files in {albumDir}.");

        if (File.Exists(coverPath) && !replace) say("cover.jpg is already there (--replace fetches it again).");
        else
        {
            var releaseId = FlacTags.First(flac, "MUSICBRAINZ_ALBUMID");
            var groupId = FlacTags.First(flac, "MUSICBRAINZ_RELEASEGROUPID");
            if (releaseId is null) notes.Add("No MusicBrainz release id in the tags, so there is nothing to look up for the front.");
            else
            {
                say($"Front cover: release {releaseId}" + (groupId is null ? "" : $", then release group {groupId}") + "...");
                byte[]? cover = null;
                try
                {
                    using var http = RipSession.NewHttp(30);
                    cover = await CoverArt.FrontAsync(http, releaseId, groupId, ct);
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                if (cover is null) notes.Add("No front cover: the Cover Art Archive has none for this release, or could not be reached. Try again later.");
                else
                {
                    var part = coverPath + ".part";
                    await File.WriteAllBytesAsync(part, cover, ct);
                    File.Move(part, coverPath, overwrite: true);
                    say($"cover.jpg written ({cover.Length / 1024} KB).");
                }
            }
        }

        var backPath = Path.Combine(albumDir, "back.jpg");
        if (File.Exists(backPath) && !replace) say("back.jpg is already there (--replace fetches it again).");
        else
        {
            if (replace && File.Exists(backPath)) File.Delete(backPath);
            await PostRip.BackArtAsync(albumDir, say, notes, ct);
        }
        return new Result(File.Exists(coverPath), File.Exists(backPath), notes);
    }
}
