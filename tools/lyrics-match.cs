#:project ../src/Deadwax.Core/Deadwax.Core.csproj
#:package Microsoft.Data.Sqlite@10.0.14
#:property PublishAot=false

// Deadwax - lyrics for a FLAC library from a local copy of LRCLIB's database.
//
//   dotnet run tools/lyrics-match.cs -- --db LRCLIB.sqlite3 LIBRARY [--report FILE] [--write] [--undo MANIFEST]
//
// His idea (2026-11-02): "we just write a small program that matches them and
// inserts them by album automagically. At least for my flac rips." LRCLIB
// publishes its whole database as an SQLite dump (lrclib.net/db-dumps); this
// reads a local copy of it, so there is no server to wait on and no rate limit.
// A tool of its own, not part of the app: only it needs SQLite. The dump is
// copyrighted lyrics: it stays on his own drives, for his own library.
//
// MATCHING, per album folder (spec §11 has the reasons):
// 1. Each track by its own tags (ARTIST, ALBUMARTIST, ALBUM, TITLE) and its
//    exact length from STREAMINFO.
// 2. Candidates by the WORDS of artist and title, through the dump's full-text
//    index (which ignores accents and splits at punctuation: "Mötley Crüe",
//    "Guns N’ Roses", "L.A. Guns" all found as written), within ±2 s of the
//    length; then artist and title compared after light normalising. Asking
//    for the artist's exact text found none of those (the first dry run's
//    28 unsure albums were nearly all that). The album name is a preference, not a
//    requirement: LRCLIB's are messy (deluxe editions, remasters). Entries
//    with a wrong length (the dump has many: "Rooster", 43 s) never qualify.
// 3. The best candidate: synced over plain, then lyrics several entries agree
//    on, then the album name, then the nearest length. Synced lyrics must fit
//    the track: the last line inside its length, enough lines to be lyrics.
//    STRICT (his choice, 2026-11-02): lyrics are used only when CONFIDENT, from
//    an entry for this very album or with at least two entries agreeing on the
//    text; and never from an album with "live" in its name unless this one is
//    live too. A wrong .lrc is worse than none.
// 4. THE ALBUM GATE: an album is written only when at least 70% of its songs
//    (instrumentals aside) have a confident match; otherwise nothing is
//    written for it and it is reported as unsure.
//
// A DRY RUN unless --write: the report (Markdown) says, album by album, what
// would be written and why not. With --write, each track is written as
// Deadwax writes lyrics after a rip (a .lrc beside the FLAC for synced lyrics
// and their words in the LYRICS tag; plain lyrics in the tag only), never over
// lyrics already there, and every change goes into a manifest that --undo
// takes back.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Deadwax.Core;
using Deadwax.Metadata;
using Microsoft.Data.Sqlite;
using Spindle;

string? db = null, library = null, report = null, undo = null;
var write = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--db": db = args[++i]; break;
        case "--report": report = args[++i]; break;
        case "--write": write = true; break;
        case "--undo": undo = args[++i]; break;
        default: library = args[i]; break;
    }
}
if (undo is not null) return Undo(undo);
if (db is null || library is null || !File.Exists(db) || !Directory.Exists(library))
{
    Console.Error.WriteLine("usage: dotnet run tools/lyrics-match.cs -- --db LRCLIB.sqlite3 LIBRARY [--report FILE] [--write] | --undo MANIFEST");
    return 2;
}
report ??= Path.Combine(Path.GetTempPath(), $"lyrics-match-{DateTime.Now:yyyyMMdd-HHmmss}.md");
var manifestPath = Path.ChangeExtension(report, ".manifest.tsv");

// The dump is opened immutable: nothing changes it, so SQLite needs no lock
// and no -shm file beside it (it is a WAL database, which read-only alone
// cannot open).
var uri = "file:" + Uri.EscapeDataString(Path.GetFullPath(db)).Replace("%2F", "/") + "?immutable=1";
using var conn = new SqliteConnection($"Data Source={uri};Mode=ReadOnly");
conn.Open();
using var query = conn.CreateCommand();
query.CommandText = """
    SELECT t.name, t.artist_name, t.album_name, t.duration, l.synced_lyrics, l.plain_lyrics, l.instrumental
    FROM tracks_fts f JOIN tracks t ON t.id = f.rowid JOIN lyrics l ON l.id = t.last_lyrics_id
    WHERE tracks_fts MATCH $match AND t.duration BETWEEN $min AND $max
    """;
var pArtist = query.Parameters.Add("$match", SqliteType.Text);
var pMin = query.Parameters.Add("$min", SqliteType.Real);
var pMax = query.Parameters.Add("$max", SqliteType.Real);

var albums = new[] { library }.Concat(Directory.EnumerateDirectories(library, "*", SearchOption.AllDirectories))
    .Where(d => Directory.EnumerateFiles(d, "*.flac").Any())
    .Order(StringComparer.Ordinal).ToList();

var md = new StringBuilder();
var manifest = new List<string>();
int albumsWritten = 0, albumsUnsure = 0, albumsDone = 0, tracksSynced = 0, tracksPlain = 0, tracksHad = 0, tracksMissing = 0;
var unsure = new List<string>();
var started = DateTime.Now;

foreach (var album in albums)
{
    var rows = new List<Row>();
    foreach (var path in Directory.EnumerateFiles(album, "*.flac").Order(StringComparer.Ordinal))
    {
        FlacMetadata flac;
        try { flac = FlacMetadata.Read(path); } catch (IOException) { continue; }
        var title = flac.First("TITLE");
        var artist = flac.First("ARTIST");
        var albumArtist = flac.First("ALBUMARTIST");
        if (string.IsNullOrWhiteSpace(title) || (artist ?? albumArtist) is null) continue;
        var row = new Row(path, title!, artist ?? albumArtist!, albumArtist, flac.First("ALBUM") ?? "", RipLyrics.Seconds(flac.StreamInfoBlock));
        row.Has = File.Exists(RipLyrics.LrcPath(path)) || !string.IsNullOrWhiteSpace(flac.First("LYRICS"));
        if (!row.Has) row.Choice = Choose(row);
        rows.Add(row);
    }
    if (rows.Count == 0) continue;

    var todo = rows.Where(r => !r.Has).ToList();
    var songs = todo.Where(r => r.Choice is not { Instrumental: true }).ToList();
    var matched = songs.Count(r => r.Choice is { Lyrics: not null });
    // Songs that have lyrics already count as matched: the gate asks whether this is the album
    // the dump knows, and a second run would otherwise judge an album on its leftovers alone
    // (one song left and missed: 0 of 1, "unsure"; 2026-11-03).
    var had = rows.Count(r => r.Has);
    var share = songs.Count == 0 ? 1.0 : (double)(matched + had) / (songs.Count + had);
    var verdict = todo.Count == 0 ? "done already" : share >= 0.7 ? (write ? "written" : "would write") : "unsure";
    var name = Path.GetRelativePath(library, album);
    md.AppendLine($"## {name}").AppendLine()
      .AppendLine($"**{verdict}**: {matched} of {songs.Count} songs matched ({share:P0}); {rows.Count(r => r.Has)} had lyrics already; {todo.Count - songs.Count} instrumental.").AppendLine();
    foreach (var r in rows)
    {
        var what = r.Has ? "has lyrics already"
            : r.Choice is { Instrumental: true } ? "instrumental"
            : r.Choice is { Lyrics: not null } c ? $"{(c.Synced ? "synced" : "plain")} ({c.Why})"
            : $"no match ({r.Choice?.Why ?? "nothing in the dump"})";
        md.AppendLine($"- `{Path.GetFileName(r.Path)}` ({r.Seconds:0.0} s): {what}");
    }
    md.AppendLine();

    if (verdict == "done already") { albumsDone++; tracksHad += rows.Count; continue; }
    tracksHad += rows.Count(r => r.Has);
    if (verdict == "unsure") { albumsUnsure++; unsure.Add($"{name} ({matched}/{songs.Count})"); tracksMissing += songs.Count; continue; }
    albumsWritten++;
    foreach (var r in songs)
    {
        if (r.Choice is not { Lyrics: { } lyrics } c) { tracksMissing++; continue; }
        if (c.Synced) tracksSynced++; else tracksPlain++;
        if (!write) continue;
        if (c.Synced)
        {
            var lrc = RipLyrics.LrcPath(r.Path);
            if (File.Exists(lrc)) continue;
            File.WriteAllText(lrc + ".new", Lrc.Tidy(lyrics) + "\n");
            File.Move(lrc + ".new", lrc, overwrite: false);
            manifest.Add($"lrc\t{lrc}");
            FlacTags.Update(r.Path, new Dictionary<string, string> { ["LYRICS"] = Lrc.Plain(lyrics) }, []);
        }
        else FlacTags.Update(r.Path, new Dictionary<string, string> { ["LYRICS"] = Lrc.Tidy(lyrics) }, []);
        manifest.Add($"tag\t{r.Path}");
    }
}

var head = new StringBuilder()
    .AppendLine($"# Lyrics from the LRCLIB dump: {(write ? "WRITTEN" : "dry run")}").AppendLine()
    .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, {albums.Count} album folders under `{library}`, {(DateTime.Now - started).TotalSeconds:0} s.").AppendLine()
    .AppendLine($"- Albums {(write ? "written" : "that would be written")}: **{albumsWritten}** ({tracksSynced} tracks synced, {tracksPlain} plain)")
    .AppendLine($"- Albums unsure (under 70% matched; nothing written): **{albumsUnsure}**")
    .AppendLine($"- Albums with lyrics on every track already: **{albumsDone}**")
    .AppendLine($"- Tracks that had lyrics already: {tracksHad}; songs left without: {tracksMissing}").AppendLine();
if (unsure.Count > 0) head.AppendLine("Unsure albums: " + string.Join("; ", unsure)).AppendLine();
File.WriteAllText(report, head.ToString() + md);
if (write) File.WriteAllLines(manifestPath, manifest);
Console.WriteLine(head.ToString().TrimEnd());
Console.WriteLine($"Report: {report}{(write ? $"\nManifest (for --undo): {manifestPath}" : "")}");
return 0;

// ---- choosing ----------------------------------------------------------------

Choice? Choose(Row row)
{
    var artists = new[] { row.Artist, row.AlbumArtist }.Where(a => !string.IsNullOrWhiteSpace(a))
        .Select(a => Words(a!)).Where(w => w.Length > 0).Distinct().ToList();
    var titleWords = Words(row.Title);
    var want = Norm(row.Title);
    var wantAlbum = Norm(row.Album);
    var candidates = new List<Candidate>();
    if (titleWords.Length == 0) return null;
    foreach (var a in artists)
    {
        pArtist.Value = $"artist_name_lower:({Fts(a)}) AND name_lower:({Fts(titleWords)})";
        pMin.Value = row.Seconds - 2; pMax.Value = row.Seconds + 2;
        using var r = query.ExecuteReader();
        while (r.Read())
        {
            if (Norm(r.GetString(0)) != want || r.IsDBNull(1) || Words(r.GetString(1)) != a) continue;
            candidates.Add(new Candidate(
                r.IsDBNull(3) ? 0 : r.GetDouble(3), r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                !r.IsDBNull(6) && r.GetBoolean(6)));
        }
    }
    // A live recording is another performance: not used for a studio track.
    var thisLive = Norm(row.Album).Split(' ').Contains("live") || Norm(row.Title).Split(' ').Contains("live");
    var liveLeftOut = candidates.RemoveAll(c => !thisLive && Norm(c.Album).Split(' ').Contains("live"));
    if (candidates.Count == 0) return liveLeftOut > 0 ? new Choice(null, false, false, $"only {liveLeftOut} entr{(liveLeftOut == 1 ? "y" : "ies")} from live albums") : null;
    if (candidates.All(c => c.Instrumental)) return new Choice(null, false, true, "instrumental in LRCLIB");

    // Synced lyrics that fit this track: the last line inside its length, and
    // enough lines to be lyrics, not a stub.
    bool Fits(string synced)
    {
        var lines = Lrc.Parse(synced);
        return lines.Count >= 4 && lines[^1].Time.TotalSeconds <= row.Seconds + 2;
    }
    var synced = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Synced) && Fits(c.Synced!)).ToList();
    if (synced.Count > 0)
    {
        var votes = synced.GroupBy(c => Lrc.Tidy(c.Synced)).ToDictionary(g => g.Key, g => g.Count());
        var best = synced.OrderByDescending(c => votes[Lrc.Tidy(c.Synced)])
            .ThenByDescending(c => Norm(c.Album) == wantAlbum)
            .ThenBy(c => Math.Abs(c.Duration - row.Seconds)).First();
        var v = votes[Lrc.Tidy(best.Synced)];
        if (v >= 2 || Norm(best.Album) == wantAlbum)
            return new Choice(best.Synced, true, false,
            $"{(Norm(best.Album) == wantAlbum ? "this album" : $"from \"{best.Album}\"")}, {Math.Round(best.Duration - row.Seconds, 1) + 0.0:+0.0;-0.0;0.0} s{(v > 1 ? $", {v} entries agree" : "")}");
        return new Choice(null, false, false, $"one synced entry, from \"{best.Album}\": not sure enough");
    }
    var plain = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Plain) && !c.Instrumental).ToList();
    var plainVotes = plain.GroupBy(c => Lrc.Tidy(c.Plain)).ToDictionary(g => g.Key, g => g.Count());
    plain = plain.Where(c => Norm(c.Album) == wantAlbum || plainVotes[Lrc.Tidy(c.Plain)] >= 2).ToList();
    if (plain.Count > 0)
    {
        var best = plain.OrderByDescending(c => Norm(c.Album) == wantAlbum).ThenByDescending(c => plainVotes[Lrc.Tidy(c.Plain)])
            .ThenBy(c => Math.Abs(c.Duration - row.Seconds)).First();
        return new Choice(best.Plain, false, false, $"{(Norm(best.Album) == wantAlbum ? "this album" : $"from \"{best.Album}\"")}, {Math.Round(best.Duration - row.Seconds, 1) + 0.0:+0.0;-0.0;0.0} s");
    }
    var badSync = candidates.Count(c => !string.IsNullOrWhiteSpace(c.Synced));
    return new Choice(null, false, false, badSync > 0 ? $"{badSync} synced entr{(badSync == 1 ? "y" : "ies")} that do not fit this track's length" : "only empty entries");
}

// Lower case, accents off, "&" as "and", punctuation and a remaster note gone,
// spaces collapsed: "Don't Let Me Be Lonely Tonight (2019 Remaster)" and
// "dont let me be lonely tonight" are one title. A live or other version note
// is kept: that is a different recording.
static string Norm(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return "";
    var t = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
    t = new string(t.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
    // Apostrophes go, as LRCLIB's own lower-case names drop them ("shes got a way"):
    // turned into a space, "She's" was "she s" and 227 titles found nothing (2026-11-03).
    t = Regex.Replace(t, "['’‘ʼ]", "").Replace("&", " and ");
    t = Regex.Replace(t, @"[\(\[][^\)\]]*remaster[^\)\]]*[\)\]]", " ");
    t = Regex.Replace(t, @"\s+-\s+.*remaster.*$", " ");
    t = Regex.Replace(t, @"[^\p{L}\p{N}]+", " ");
    return Regex.Replace(t, @"\s+", " ").Trim();
}

// The words of a name for matching it: Norm's, without "and" and "the",
// which the index drops or a submitter adds ("&", "The Alan Parsons Project").
static string Words(string s) =>
    string.Join(' ', Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not "and" and not "the"));

// Each word quoted for FTS5, so none is read as an operator (AND, NOT, NEAR).
static string Fts(string words) => string.Join(' ', words.Split(' ').Select(w => "\"" + w + "\""));

static int Undo(string manifest)
{
    var lines = File.ReadAllLines(manifest);
    int lrcs = 0, tags = 0;
    foreach (var line in lines)
    {
        var parts = line.Split('\t', 2);
        if (parts.Length != 2) continue;
        if (parts[0] == "lrc" && File.Exists(parts[1])) { File.Delete(parts[1]); lrcs++; }
        if (parts[0] == "tag" && File.Exists(parts[1])) { FlacTags.Update(parts[1], new Dictionary<string, string>(), ["LYRICS"]); tags++; }
    }
    Console.WriteLine($"Undone: {lrcs} .lrc files removed, LYRICS taken out of {tags} files.");
    return 0;
}

sealed record Candidate(double Duration, string Album, string? Synced, string? Plain, bool Instrumental);
sealed record Choice(string? Lyrics, bool Synced, bool Instrumental, string Why);
sealed class Row(string path, string title, string artist, string? albumArtist, string album, double seconds)
{
    public string Path { get; } = path;
    public string Title { get; } = title;
    public string Artist { get; } = artist;
    public string? AlbumArtist { get; } = albumArtist;
    public string Album { get; } = album;
    public double Seconds { get; } = seconds;
    public bool Has { get; set; }
    public Choice? Choice { get; set; }
}
