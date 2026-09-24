namespace Deadwax.Drive;

/// One track in a disc's table of contents.
///
/// Positions are LSNs: sectors counted from the start of the program area, so
/// track 1 of most discs starts at 0. The 150-sector (2 s) lead-in that every
/// disc-ID formula adds back is NOT included here; each formula adds it itself,
/// which keeps the one number that means "where the track is" in one form.
public sealed record TocTrack(int Number, int StartLsn, bool IsAudio);

/// A disc's table of contents: its tracks in order, and where the lead-out starts.
public sealed record Toc
{
    /// Sectors between the end of one session and the start of the next. On an
    /// enhanced CD (audio session, then a data session) the audio really ends
    /// this far before the data track starts, and every disc-ID scheme uses that
    /// point, not the disc's lead-out, as the end of the audio.
    public const int SessionGap = 11400;

    public const int SectorsPerSecond = 75;
    public const int LeadIn = 150;

    public IReadOnlyList<TocTrack> Tracks { get; }
    public int LeadoutLsn { get; }

    public Toc(IReadOnlyList<TocTrack> tracks, int leadoutLsn)
    {
        if (tracks.Count == 0) throw new ArgumentException("A TOC needs at least one track.", nameof(tracks));
        for (var i = 1; i < tracks.Count; i++)
        {
            if (tracks[i].Number != tracks[i - 1].Number + 1)
                throw new ArgumentException($"Track numbers are not consecutive at track {tracks[i].Number}.", nameof(tracks));
            if (tracks[i].StartLsn <= tracks[i - 1].StartLsn)
                throw new ArgumentException($"Track {tracks[i].Number} does not start after track {tracks[i - 1].Number}.", nameof(tracks));
        }
        if (leadoutLsn <= tracks[^1].StartLsn)
            throw new ArgumentException("The lead-out must come after the last track.", nameof(leadoutLsn));

        Tracks = tracks;
        LeadoutLsn = leadoutLsn;
    }

    public int FirstTrack => Tracks[0].Number;
    public int LastTrack => Tracks[^1].Number;

    /// The tracks that disc IDs describe. Data tracks at the END of the disc (an
    /// enhanced CD's data session) are left out; a data track anywhere else stays,
    /// because MusicBrainz and AccurateRip both count it there.
    public IReadOnlyList<TocTrack> IdTracks
    {
        get
        {
            var end = Tracks.Count;
            while (end > 1 && !Tracks[end - 1].IsAudio) end--;
            return end == Tracks.Count ? Tracks : Tracks.Take(end).ToList();
        }
    }

    /// Where the audio ends, as disc IDs see it: the lead-out, or on an enhanced
    /// CD the start of the data session less the gap between sessions.
    public int AudioLeadoutLsn
    {
        get
        {
            var ids = IdTracks;
            return ids.Count == Tracks.Count ? LeadoutLsn : Tracks[ids.Count].StartLsn - SessionGap;
        }
    }

    /// First sector after the given track: the next track's start, or the lead-out.
    public int EndLsn(TocTrack track)
    {
        var i = IndexOf(track.Number);
        return i + 1 < Tracks.Count ? Tracks[i + 1].StartLsn : LeadoutLsn;
    }

    public TocTrack Track(int number) => Tracks[IndexOf(number)];

    private int IndexOf(int number)
    {
        var i = number - FirstTrack;
        if (i < 0 || i >= Tracks.Count) throw new ArgumentOutOfRangeException(nameof(number), number, "No such track on this disc.");
        return i;
    }
}
