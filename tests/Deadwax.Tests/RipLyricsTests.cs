using System.Net;
using System.Text;
using Deadwax.Core;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Tests;

/// Lyrics after a rip (RipLyrics, LyricsQueue), against a LRCLIB that is never
/// the real one and FLACs made as a rip makes them. The lyrics are placeholders.
public sealed class RipLyricsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deadwax-lyrics-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private const string Synced = "[00:01.00] line one\n[00:02.50] line two";

    /// Two seconds of silence, CD audio, tagged.
    private string Track(string name, string artist = "The Band", string title = "Song", string album = "The Album")
    {
        var path = Path.Combine(_dir, name + ".flac");
        FlacWriter.Encode(path, new byte[44100 * 4 * 2]);
        FlacWriter.WriteTags(path, [new Tag("ARTIST", artist), new Tag("TITLE", title), new Tag("ALBUM", album)]);
        return path;
    }

    /// Answers a /get with `get` (null: 404) and a /search with `search`, or
    /// fails with `status`; counts the questions.
    private sealed class FakeLrcLib(string? get, string search = "[]", HttpStatusCode? status = null) : HttpMessageHandler
    {
        public int Asked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked++;
            if (status is { } s) return Task.FromResult(new HttpResponseMessage(s));
            var isGet = request.RequestUri!.AbsolutePath.EndsWith("/get");
            if (isGet && get is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(isGet ? get! : search, Encoding.UTF8, "application/json") });
        }
    }

    private static string Answer(double seconds, string title = "Song", string artist = "The Band", bool synced = true, string plain = "line one\nline two", bool instrumental = false) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            // An id of its own per song, as LRCLIB gives (results are told apart by it).
            ["id"] = title.GetHashCode() & 0x7fffffff, ["trackName"] = title, ["artistName"] = artist, ["albumName"] = "The Album", ["duration"] = seconds,
            ["instrumental"] = instrumental, ["plainLyrics"] = plain, ["syncedLyrics"] = synced ? Synced : null,
        });

    private static LrcLibClient Lrc(HttpMessageHandler h) => new(h, [TimeSpan.Zero, TimeSpan.Zero], "Deadwax-tests");

    [Fact]
    public void A_track_is_asked_by_its_tags_and_exact_length()
    {
        Track("01 Song");
        var job = Assert.Single(RipLyrics.JobsFor(_dir));
        Assert.Equal(("The Band", "Song", "The Album"), (job.Artist, job.Title, job.Album));
        Assert.Equal(2.0, job.Seconds, 3);
    }

    [Fact]
    public async Task Synced_lyrics_that_fit_become_a_lrc_and_the_words_in_the_tag()
    {
        var path = Track("01 Song");
        var job = RipLyrics.JobsFor(_dir)[0];
        var (outcome, _) = await RipLyrics.FetchAsync(Lrc(new FakeLrcLib(Answer(3))), job, plainToo: true);   // 1 s off: fits
        Assert.Equal(LyricsOutcome.Written, outcome);
        Assert.Equal(Synced + "\n", File.ReadAllText(RipLyrics.LrcPath(path)));
        Assert.Equal("line one\nline two", FlacTags.First(path, "LYRICS"));
        Assert.Equal("Song", FlacTags.First(path, "TITLE"));                 // the other tags kept
        Assert.Empty(Directory.GetFiles(_dir, ".*.new"));
    }

    [Fact]
    public async Task A_near_miss_is_left_for_Marginal_and_nothing_is_written()
    {
        var path = Track("01 Song");
        var (outcome, note) = await RipLyrics.FetchAsync(Lrc(new FakeLrcLib(Answer(30))), RipLyrics.JobsFor(_dir)[0], true);   // 28 s off
        Assert.Equal(LyricsOutcome.NotSure, outcome);
        Assert.Contains("Marginal", note);
        Assert.False(File.Exists(RipLyrics.LrcPath(path)));
        Assert.Null(FlacTags.First(path, "LYRICS"));
    }

    [Fact]
    public async Task Plain_lyrics_go_to_the_tag_only_when_wanted()
    {
        var path = Track("01 Song");
        var job = RipLyrics.JobsFor(_dir)[0];
        Assert.Equal(LyricsOutcome.NoneFound, (await RipLyrics.FetchAsync(Lrc(new FakeLrcLib(Answer(2, synced: false))), job, plainToo: false)).Outcome);
        Assert.Null(FlacTags.First(path, "LYRICS"));
        Assert.Equal(LyricsOutcome.Written, (await RipLyrics.FetchAsync(Lrc(new FakeLrcLib(Answer(2, synced: false))), job, plainToo: true)).Outcome);
        Assert.Equal("line one\nline two", FlacTags.First(path, "LYRICS"));
        Assert.False(File.Exists(RipLyrics.LrcPath(path)));
    }

    [Fact]
    public async Task Lyrics_already_there_are_never_written_over()
    {
        var path = Track("01 Song");
        File.WriteAllText(RipLyrics.LrcPath(path), "[00:01.00] mine");
        var fake = new FakeLrcLib(Answer(2));
        Assert.Equal(LyricsOutcome.Skipped, (await RipLyrics.FetchAsync(Lrc(fake), RipLyrics.JobsFor(_dir)[0], true)).Outcome);
        Assert.Equal(0, fake.Asked);
        Assert.Equal("[00:01.00] mine", File.ReadAllText(RipLyrics.LrcPath(path)));
    }

    [Fact]
    public async Task An_instrumental_and_nothing_found_are_answers()
    {
        Track("01 Song");
        var job = RipLyrics.JobsFor(_dir)[0];
        Assert.Equal(LyricsOutcome.NoneFound, (await RipLyrics.FetchAsync(Lrc(new FakeLrcLib(Answer(2, instrumental: true, synced: false))), job, true)).Outcome);
        Assert.Equal(LyricsOutcome.NoneFound, (await RipLyrics.FetchAsync(Lrc(new FakeLrcLib(null)), job, true)).Outcome);
    }

    [Fact]
    public async Task What_LRCLIB_does_not_answer_waits_in_the_queue_and_is_asked_again_later()
    {
        Track("01 Song"); Track("02 Other", title: "Other");
        var queue = new LyricsQueue(Path.Combine(_dir, "queue.json"), TimeSpan.Zero);
        queue.Add(RipLyrics.JobsFor(_dir));
        queue.Add(RipLyrics.JobsFor(_dir));                     // the same tracks twice: once each
        Assert.Equal(2, queue.Count);

        var said = new List<string>();
        var now = DateTimeOffset.Now;
        await queue.RunAsync(Lrc(new FakeLrcLib(null, status: HttpStatusCode.ServiceUnavailable)), true, said.Add, now);
        Assert.Equal(2, queue.Count);                            // kept, not given up
        Assert.Contains(said, s => s.Contains("did not answer"));

        // Not due again for ten minutes: a run now asks nothing.
        var fake = new FakeLrcLib(Answer(2));
        await queue.RunAsync(Lrc(fake), true, said.Add, now.AddMinutes(1));
        Assert.Equal(0, fake.Asked);

        // Later, LRCLIB answers: written, and the queue empties (its file goes).
        var answered = new FakeLrcLib(Answer(2, title: "Song"), search: "[" + Answer(2, title: "Other") + "]");
        var (_, written) = await queue.RunAsync(Lrc(answered), true, said.Add, now.AddMinutes(11));
        Assert.Equal(2, written);
        Assert.Equal(0, queue.Count);
        Assert.False(File.Exists(Path.Combine(_dir, "queue.json")));
    }

    [Fact]
    public async Task Three_unanswered_in_a_row_and_the_rest_wait()
    {
        foreach (var n in new[] { "01", "02", "03", "04", "05" }) Track(n + " T", title: "T" + n);
        var queue = new LyricsQueue(Path.Combine(_dir, "queue.json"), TimeSpan.Zero);
        queue.Add(RipLyrics.JobsFor(_dir));
        var fake = new FakeLrcLib(null, status: HttpStatusCode.BadGateway);
        var said = new List<string>();
        await queue.RunAsync(Lrc(fake), true, said.Add);
        Assert.Contains(said, s => s.Contains("not answering"));
        Assert.Equal(5, queue.Count);
        Assert.Equal(3 * 3, fake.Asked);                         // three tracks, three tries each (the client's), then stop
    }

    [Fact]
    public void A_queue_that_cannot_be_read_is_an_empty_one()
    {
        var file = Path.Combine(_dir, "queue.json");
        File.WriteAllText(file, "{ not json");
        Assert.Equal(0, new LyricsQueue(file).Count);
    }
}
