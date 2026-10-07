using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Deadwax.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public enum Stage { Waiting, Scanning, Disc, Ripping, Intake }

/// The palette's status colours, in one place, so a state is never colour alone
/// but always colour plus a word (the mockup's rule).
public static class Tone
{
    public static readonly IBrush Good = Brush.Parse("#3CC850");
    public static readonly IBrush Warn = Brush.Parse("#FFC94D");
    public static readonly IBrush Bad = Brush.Parse("#EB4646");
    public static readonly IBrush Text = Brush.Parse("#DCDCDC");
    public static readonly IBrush Dim = Brush.Parse("#8C8C94");
    public static readonly IBrush Waiting = Brush.Parse("#6E6E76");
    public static readonly IBrush Unread = Brush.Parse("#3C3C42");
    /// The map's colours are calmer than the text's: a whole disc of the bright
    /// green was a loud block in the neutral frame (2026-09-24), and "reading"
    /// needed to stand out from "not read yet".
    public static readonly IBrush Reading = Brush.Parse("#8A6A1E");
    public static readonly IBrush MapGood = Brush.Parse("#2D8F46");
    public static readonly IBrush ActiveRow = Brush.Parse("#26262C");
    public static readonly IBrush Selected = Brush.Parse("#2C2C30");
    public static readonly IBrush Unselected = Brush.Parse("#232326");
    public static readonly IBrush Amber = Brush.Parse("#FFB000");
    public static readonly IBrush RowRule = Brush.Parse("#3C3C42");
}

/// One line of the self-description card: a track and the title he gives it.
public sealed class DescribeTrack : Observable
{
    private string _title = "";
    public int Number { get; init; }
    public string Length { get; init; } = "";
    public string Title { get => _title; set => Set(ref _title, value); }
}

public sealed class ReleaseRow : Observable
{
    public required Deadwax.Metadata.ReleaseCandidate Candidate { get; init; }
    public string Id => Candidate.Id;
    public required string Title { get; init; }
    /// MusicBrainz's disambiguation note: what tells two releases of the same
    /// name apart, so it belongs in the list even though it stays out of the
    /// folder name.
    public string? Note { get; init; }
    public bool HasNote => Note is not null;
    public required string Detail { get; init; }
    /// Label, catalog number and barcode: what can be checked against the case.
    public required string Printed { get; init; }
    public bool HasPrinted => Printed.Length > 0;
    public bool IsSet => Candidate.MediaCount > 1;

    private bool _barcodeMatch;
    public bool BarcodeMatch { get => _barcodeMatch; set => Set(ref _barcodeMatch, value); }

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Raise(nameof(Background), nameof(Border)); }
    }

    public IBrush Background => IsSelected ? Tone.Selected : Tone.Unselected;
    public IBrush Border => IsSelected ? Tone.Amber : Tone.RowRule;
}

public sealed class TrackRow : Observable
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Length { get; init; }
    public string NumberText => Number.ToString("D2");

    private string _test = "", _copy = "", _speed = "", _status = "Waiting", _detail = "";
    private IBrush _statusBrush = Tone.Waiting;
    private IBrush _fg = Tone.Waiting;
    private IBrush _bg = Avalonia.Media.Brushes.Transparent;
    private double _progress;
    private bool _active;

    public string TestCrc { get => _test; set => Set(ref _test, value); }
    public string CopyCrc { get => _copy; set => Set(ref _copy, value); }
    public string Speed { get => _speed; set => Set(ref _speed, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public IBrush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public IBrush Foreground { get => _fg; set => Set(ref _fg, value); }
    public IBrush Background { get => _bg; set => Set(ref _bg, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Active { get => _active; set { if (Set(ref _active, value)) Raise(nameof(NotActive)); } }
    public bool NotActive => !Active;
}

/// One cell of the read map: about two seconds of the disc.
public sealed class MapCell : Observable
{
    private IBrush _fill = Tone.Unread;
    public IBrush Fill { get => _fill; set => Set(ref _fill, value); }
}

public sealed class StepRow
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required bool Ok { get; init; }
    public IBrush Brush => Ok ? Tone.Good : Tone.Warn;
    public string Mark => Ok ? "✓" : "!";
}

/// A row of the drive-speed list; null = the drive's own choice.
public sealed record SpeedChoice(int? Limit)
{
    public override string ToString() => Limit is { } n ? $"{n}x" : "Full speed";
}

public sealed class MainViewModel : Observable
{
    private Stage _stage = Stage.Waiting;
    public Stage Stage
    {
        get => _stage;
        set
        {
            if (Set(ref _stage, value))
                Raise(nameof(IsWaiting), nameof(IsScanning), nameof(IsDisc), nameof(IsRipping), nameof(IsIntake),
                      nameof(ShowDiscScreen), nameof(DiscStage), nameof(RipStage), nameof(IntakeStage));
        }
    }

    public bool IsWaiting => Stage == Stage.Waiting;
    public bool IsScanning => Stage == Stage.Scanning;
    public bool IsDisc => Stage == Stage.Disc;
    public bool IsRipping => Stage == Stage.Ripping;
    public bool IsIntake => Stage == Stage.Intake;
    public bool ShowDiscScreen => Stage is Stage.Waiting or Stage.Scanning or Stage.Disc;
    public string DiscStage => ShowDiscScreen ? "current" : "";
    public string RipStage => IsRipping ? "current" : "";
    public string IntakeStage => IsIntake ? "current" : "";

    private string _drive = "", _status = "", _error = "";
    public string DriveText { get => _drive; set => Set(ref _drive, value); }
    public string StatusText { get => _status; set => Set(ref _status, value); }
    public string ErrorText { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => ErrorText.Length > 0;

    /// The drive has no read offset on file; the Disc screen offers to measure it.
    private bool _needsOffset, _measuringOffset;
    private string _offsetText = "";
    public bool NeedsOffset { get => _needsOffset; set => Set(ref _needsOffset, value); }
    public bool MeasuringOffset { get => _measuringOffset; set => Set(ref _measuringOffset, value); }
    public string OffsetText { get => _offsetText; set => Set(ref _offsetText, value); }

    // The Disc screen.
    private Bitmap? _cover;
    public Bitmap? Cover { get => _cover; set { if (Set(ref _cover, value)) Raise(nameof(HasCover), nameof(NoCover)); } }
    public bool HasCover => Cover is not null;
    public bool NoCover => Cover is null;

    private string _tracks = "", _discType = "Pressed CD", _gap = "", _catalog = "", _discId = "", _ar = "";
    public string TracksText { get => _tracks; set => Set(ref _tracks, value); }
    public string DiscTypeText { get => _discType; set => Set(ref _discType, value); }
    public string GapText { get => _gap; set => Set(ref _gap, value); }
    public string CatalogText { get => _catalog; set => Set(ref _catalog, value); }
    public string DiscIdText { get => _discId; set => Set(ref _discId, value); }
    public string AccurateRipText { get => _ar; set => Set(ref _ar, value); }

    public ObservableCollection<ReleaseRow> Releases { get; } = [];

    /// The self-description card (DiscDescription).
    private bool _describeOpen;
    private string _describeArtist = "", _describeAlbum = "", _describeYear = "", _describeNote = "";
    public bool DescribeOpen { get => _describeOpen; set => Set(ref _describeOpen, value); }
    public string DescribeArtist { get => _describeArtist; set => Set(ref _describeArtist, value); }
    public string DescribeAlbum { get => _describeAlbum; set => Set(ref _describeAlbum, value); }
    public string DescribeYear { get => _describeYear; set => Set(ref _describeYear, value); }
    public string DescribeNote { get => _describeNote; set => Set(ref _describeNote, value); }
    public ObservableCollection<DescribeTrack> DescribeTracks { get; } = [];
    public string ReleasesHeading => Releases.Count switch
    {
        0 => "This disc is not on any MusicBrainz release.",
        1 => "The MusicBrainz release that matches this disc ID.",
        _ => $"{Releases.Count} MusicBrainz releases match this disc ID. Pick the one in your hands.",
    };
    public void ReleasesChanged() => Raise(nameof(ReleasesHeading));

    private string _originalYear = "", _editionYear = "", _yearNote = "", _output = "", _folderPreview = "";
    private bool _useOriginal = true, _yearChoice, _isSet;
    private bool? _setChoice;
    private string _setChoiceNote = "";
    public string OriginalYear { get => _originalYear; set { if (Set(ref _originalYear, value)) Raise(nameof(OriginalLabel)); } }
    public string EditionYear { get => _editionYear; set { if (Set(ref _editionYear, value)) Raise(nameof(EditionLabel)); } }
    public string OriginalLabel => $"{OriginalYear}, the album's first release";
    public string EditionLabel => $"{EditionYear}, this edition";
    public bool HasYearChoice { get => _yearChoice; set => Set(ref _yearChoice, value); }
    public bool UseOriginal { get => _useOriginal; set { if (Set(ref _useOriginal, value)) { Raise(nameof(UseEdition)); YearChanged?.Invoke(); } } }
    public bool UseEdition { get => !_useOriginal; set => UseOriginal = !value; }
    public string YearNote { get => _yearNote; set => Set(ref _yearNote, value); }
    public string OutputFolder { get => _output; set => Set(ref _output, value); }
    public string OutputFiles { get => _folderPreview; set => Set(ref _folderPreview, value); }
    public bool IsSetDisc { get => _isSet; set => Set(ref _isSet, value); }
    /// What to do with a disc of a set: true = file it as its own album
    /// (music-unbox), false = keep the set together, null = not chosen yet.
    /// There is no default on purpose (his call, 2026-10-06): a compilation
    /// and a box of reissued albums want opposite answers, and a wrong
    /// default refiles a disc silently. The choice is remembered per set, so
    /// disc 2 and 3 inherit what was picked for disc 1.
    public bool? SetChoice
    {
        get => _setChoice;
        set { if (Set(ref _setChoice, value)) { Raise(nameof(Unbox), nameof(KeepSet)); SetChoiceChanged?.Invoke(); } }
    }
    /// The two radio buttons. A RadioButton writes false to the one being
    /// left; that must not clear the choice, so only true is acted on.
    public bool Unbox { get => _setChoice == true; set { if (value) SetChoice = true; } }
    public bool KeepSet { get => _setChoice == false; set { if (value) SetChoice = false; } }
    public string SetChoiceNote { get => _setChoiceNote; set => Set(ref _setChoiceNote, value); }
    public event Action? SetChoiceChanged;
    public event Action? YearChanged;

    /// The drive speed cap, for a disc that vibrates: one of Speeds, where
    /// "Full speed" is no cap. Kept in settings (SlowSpin + SlowSpinSpeed).
    public static readonly IReadOnlyList<SpeedChoice> AllSpeeds =
        [new(null), new(16), new(8), new(4), new(2), new(1)];
    public IReadOnlyList<SpeedChoice> Speeds => AllSpeeds;   // an instance property, for the binding
    private SpeedChoice _driveSpeed = AllSpeeds[0];
    public SpeedChoice DriveSpeed { get => _driveSpeed; set { if (Set(ref _driveSpeed, value)) SpeedChanged?.Invoke(); } }
    public event Action? SpeedChanged;

    private bool _ejectAfterRip;
    public bool EjectAfterRip { get => _ejectAfterRip; set { if (Set(ref _ejectAfterRip, value)) EjectChanged?.Invoke(); } }
    public event Action? EjectChanged;

    /// A newer release on GitHub: the title-bar button, and its state while
    /// the download runs.
    private string _updateLabel = "";
    private string _updateTip = "";
    private bool _updating;
    public string UpdateLabel { get => _updateLabel; set { if (Set(ref _updateLabel, value)) Raise(nameof(HasUpdate)); } }
    public string UpdateTip { get => _updateTip; set => Set(ref _updateTip, value); }
    public bool HasUpdate => _updateLabel.Length > 0;
    public bool Updating { get => _updating; set => Set(ref _updating, value); }

    private bool _canRip;
    public bool CanRip { get => _canRip; set => Set(ref _canRip, value); }

    /// The subchannel read (cdrdao, about two minutes) runs behind the Disc
    /// screen; ripping waits for it.
    private string _subchannel = "";
    public string SubchannelText { get => _subchannel; set { if (Set(ref _subchannel, value)) Raise(nameof(HasSubchannelText)); } }
    public bool HasSubchannelText => SubchannelText.Length > 0;

    private string _note = "";
    private bool _keepNote;
    public string NoteText { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNoteChoice), nameof(KeepNoteLabel)); } }
    public bool HasNoteChoice => NoteText.Length > 0;
    public string KeepNoteLabel => $"Keep MusicBrainz's note “{NoteText}” in the folder name";
    public bool KeepNote { get => _keepNote; set { if (Set(ref _keepNote, value)) YearChanged?.Invoke(); } }

    /// A folder left behind by a rip that stopped partway, offered for removal.
    private string _partial = "";
    public string PartialAlbum { get => _partial; set { if (Set(ref _partial, value)) Raise(nameof(HasPartialAlbum)); } }
    public bool HasPartialAlbum => PartialAlbum.Length > 0;

    private bool _showLog;
    public bool ShowLog { get => _showLog; set { if (Set(ref _showLog, value)) Raise(nameof(LogButtonText)); } }
    public string LogButtonText => ShowLog ? "Hide engine log" : "Show engine log";

    // The Rip screen.
    private string _albumTitle = "", _albumLine = "", _now = "", _speed = "", _elapsed = "";
    private double _overall;
    public string AlbumTitle { get => _albumTitle; set => Set(ref _albumTitle, value); }
    public string AlbumLine { get => _albumLine; set => Set(ref _albumLine, value); }
    public string NowText { get => _now; set => Set(ref _now, value); }
    public string SpeedText { get => _speed; set => Set(ref _speed, value); }
    public string ElapsedText { get => _elapsed; set => Set(ref _elapsed, value); }
    public double Overall { get => _overall; set => Set(ref _overall, value); }
    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public ObservableCollection<MapCell> Map { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    // The Intake screen.
    private string _verdict = "", _verdictDetail = "", _albumDir = "";
    private bool _allGood;
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; set => Set(ref _verdictDetail, value); }
    public bool AllGood { get => _allGood; set { if (Set(ref _allGood, value)) Raise(nameof(VerdictBrush), nameof(VerdictMark)); } }
    public IBrush VerdictBrush => AllGood ? Tone.Good : Tone.Warn;
    public string VerdictMark => AllGood ? "✓" : "!";
    public string AlbumDirectory { get => _albumDir; set => Set(ref _albumDir, value); }
    public ObservableCollection<StepRow> Steps { get; } = [];
    public ObservableCollection<string> Files { get; } = [];
}
