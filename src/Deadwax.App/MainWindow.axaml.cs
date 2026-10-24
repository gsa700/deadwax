using System.Net.Http;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Deadwax.Core;
using Deadwax.Verify;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.App;

public sealed partial class MainWindow : Window
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// His library, where rips go since G5 passed (2026-09-24), and whose artist
    /// spellings every rip follows.
    private string Library => string.IsNullOrWhiteSpace(_settings.Library) ? Path.Combine(Home, "Music") : _settings.Library;

    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private PreparedDisc? _disc;
    private ReleasePlan? _plan;
    private CancellationTokenSource? _work;
    private int _planVersion;
    private byte[]? _coverBytes;
    private string? _libraryMatch;

    private readonly Settings _settings = Settings.Load();

    public MainWindow()
    {
        InitializeComponent();
        Filing.Current = _settings.ToFiling();
        if (_settings.EmbedCover is null) { _settings.EmbedCover = false; _settings.Save(); }   // decided once, not re-guessed
        Width = Math.Max(_settings.Width, MinWidth);
        Height = Math.Max(_settings.Height, MinHeight);
        // Maximized only once placed (Opened), so it maximizes on the screen it
        // was on rather than the primary one.
        if (_settings.Maximized && Program.NativeWayland) WindowState = WindowState.Maximized;
        PositionChanged += (_, _) => { if (_placed && WindowState == WindowState.Normal) _normalPosition = Position; };
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) ApplyCorners(); };
        ApplyCorners();
        DataContext = _vm;
        _vm.YearChanged += UpdateOutputPreview;
        _vm.DriveSpeed = _settings.SlowSpin
            ? MainViewModel.AllSpeeds.FirstOrDefault(c => c.Limit == _settings.SlowSpinSpeed) ?? new SpeedChoice(Math.Max(1, _settings.SlowSpinSpeed))
            : MainViewModel.AllSpeeds[0];
        _vm.SpeedChanged += OnSpeedChanged;
        _vm.EjectAfterRip = _settings.EjectAfterRip;
        _vm.EjectChanged += () => { _settings.EjectAfterRip = _vm.EjectAfterRip; _settings.Save(); };
        _vm.AlwaysTwoPasses = _settings.AlwaysTwoPasses;
        _vm.TwoPassesChanged += () => { _settings.AlwaysTwoPasses = _vm.AlwaysTwoPasses; _settings.Save(); };
        _vm.SetChoiceChanged += UpdateOutputPreview;    // Rip disc enables once a set has its answer
        _vm.Log.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
        FollowTheCurrentTrack();
        UseMacTitleBar();
        SetUpMacMenu();
        BuildWindowButtons();
        AddHandler(PointerPressedEvent, OnPointerPressedForResize, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedForResize, RoutingStrategies.Tunnel);
        TopBar.PointerPressed += OnTitleBarPressed;

        // "No disc" until one is read, then the drive and its offset (ShowDisc).
        // Not the device path: "\\.\D:" on Windows looked like an error, and
        // "/dev/sr0" told him nothing either (2026-10-13, his call).
        _vm.DriveText = NoDisc;
        _vm.StatusText = "Put a CD in the drive.";
        _watch.Tick += async (_, _) => await WatchAsync();
        Opened += async (_, _) =>
        {
            RestorePosition();
            if (_settings.Maximized) WindowState = WindowState.Maximized;
            _placed = true;
            WaylandShell.ClaimIdentity(this);   // the dash's icon, on native Wayland
            // Installing and uninstalling, BEFORE the drive is first touched
            // (InstallFlow): true when this run ends here.
            if (await InstallFlow.BeforeStartAsync(this)) return;
            _watch.Start();
            await WatchAsync();
            _ = CheckForUpdateAsync();
        };
        Closing += (_, _) =>
        {
            _work?.Cancel();
            // Hand the disc back to macOS: Deadwax unmounted it to read it, and
            // an unmounted audio CD stays in after Deadwax quits, the drive's
            // own button only asking macOS to eject (his test, 2026-10-20).
            if (OperatingSystem.IsMacOS() && !_vm.IsRipping && CdDrive.FoundDevice is { } device
                && !MacDisc.Mount(device, out var why))
                Console.Error.WriteLine($"[drive] could not hand {device} back to macOS: {why}");
            // The size of the normal window, not the maximized one, so leaving
            // maximized restores to what he last chose.
            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.Width = Bounds.Width;
                _settings.Height = Bounds.Height;
                _normalPosition = Position;
            }
            // On native Wayland the position is unknowable (0,0): writing it
            // down would throw away nothing useful but mislead an X11 session.
            if (!Program.NativeWayland && _normalPosition is { } p) (_settings.WindowX, _settings.WindowY) = (p.X, p.Y);
            _settings.Save();
        };
    }

    // ------------------------------------------------------------ the disc

    /// Every three seconds while waiting: is there a disc? And while a disc is
    /// on screen, has it gone? udev knows without spinning the drive up.
    private async Task WatchAsync()
    {
        // Not while the drive is busy: a measurement holds it too, and asking
        // then reads as "no disc", so the same CD looked new when it ended and
        // was measured again (2026-10-20, his screenshots).
        if (_vm.IsScanning || _vm.IsRipping || _vm.MeasuringOffset) return;
        var (present, audio) = await MediaAsync(CdDrive.DefaultDevice);
        _vm.HasDisc = present;
        if (!present) _vm.DriveText = NoDisc;
        if (_vm.NeedsOffset)
        {
            // The one-time setup: no scan (it would only fail on the missing
            // offset again); just say whether there is a CD to measure from.
            var hasCd = present && audio > 0;
            if (hasCd) _vm.DriveText = CdNotMeasured;
            var changed = hasCd != _setupHadCd;
            _setupHadCd = hasCd;
            if (!changed || _vm.MeasuringOffset) return;
            if (hasCd && _offsetCds > 0) { await MeasureOffsetAsync(); return; }   // the next CD of a measurement already begun
            if (!hasCd && _offsetCds > 0) _vm.OffsetText = NextCd;
            else _vm.OffsetText = hasCd ? "A CD is in. Measure when you're ready." : PutInACd;
            return;
        }
        if (_vm.IsIntake)
        {
            // The summary stays until the ripped disc has left the drive; the
            // next CD in after that is read without "Eject, next disc" (his
            // second Jackson Browne disc sat behind the summary, 2026-10-21).
            // A disc never taken out keeps the summary, so it is not read twice.
            if (!present) _intakeEmptied = true;
            else if (_intakeEmptied && audio > 0) await ScanAsync();
            return;
        }
        if (_vm.IsWaiting && present && audio > 0 && !_vm.HasError) await ScanAsync();
        else if (_vm.IsWaiting && present && audio == 0) _vm.StatusText = "That disc has no audio tracks.";
        else if (!present && _vm.IsDisc) Reset("Put a CD in the drive.");
        else if (!present && _vm.IsWaiting)
        {
            _vm.ErrorText = "";
            _vm.StatusText = "Put a CD in the drive.";
        }
    }

    private const string NoDisc = "No disc";
    // The bar during the one-time setup: there is a CD, but no offset to name with the drive
    // yet ("No disc" there contradicted the setup's "A CD is in", 2026-10-21).
    private const string CdNotMeasured = "CD in · offset not measured yet";
    private const string PutInACd = "Put in a well-known commercial CD (a big-selling album is best), then Measure.";
    private bool _setupHadCd;

    private bool _placed;
    private PixelPoint? _normalPosition;

    /// Puts the window back where it was (WindowPlacement's rules).
    private void RestorePosition()
    {
        if (WindowPlacement.Restore(this, _settings.WindowX, _settings.WindowY)) _normalPosition = Position;
        else WindowPlacement.FitNew(this);
    }

    /// 12 px corners on a normal window; square when maximized (or minimized,
    /// full screen): the window is transparent, so a rounded corner on a
    /// maximized window shows the desktop through it (AlbumWall, 2026-09-20).
    private void ApplyCorners() =>
        Surface.CornerRadius = new CornerRadius(
            WindowState == WindowState.Normal && !OperatingSystem.IsMacOS() ? 12 : 0);   // macOS rounds its own

    /// Asks the drive (libcdio: open, TOC, count), off the UI thread. Until
    /// 0.2.16 this asked udev, which exists only on Linux.
    private static Task<(bool Present, int Audio)> MediaAsync(string device) =>
        Task.Run(() => CdDrive.Media(device));

    /// The disc in seconds (TOC, disc ID, the library, MusicBrainz), then
    /// cdrdao's two-minute subchannel read behind the Disc screen while he
    /// chooses. Before 2026-09-24 the screen waited for cdrdao, and a disc
    /// already in the library was only named as such two minutes later.
    private async Task ScanAsync()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        ClearDisc();
        _vm.Stage = Stage.Scanning;
        _vm.ErrorText = "";
        _vm.PartialAlbum = "";
        _vm.StatusText = "Reading the disc...";
        try
        {
            var disc = await Task.Run(() => RipSession.PrepareAsync(null, null, _ => { }, readSubchannel: false, speedLimit: SpeedLimit, ct: ct), ct);
            _disc = disc;
            _libraryMatch = await Task.Run(() => LibraryIndex.FindDisc(Library, disc.DiscId), ct);
            ShowDisc(disc);
            _vm.Stage = Stage.Disc;
            if (_libraryMatch is not null)
                _vm.ErrorText = $"Already in your library: {Path.GetRelativePath(Library, _libraryMatch)}";

            var first = _vm.Releases.FirstOrDefault();
            if (first is not null && _vm.Releases.Count == 1) _ = ChooseAsync(first);
            else if (first is null) _vm.ErrorText = "MusicBrainz has no release for this disc. Paste a release link below, or describe the disc yourself.";

            if (_libraryMatch is null)
            {
                // A speed chosen during the TOC read had no disc to go to yet.
                if (_disc == disc && disc.SpeedLimit != SpeedLimit) _disc = disc = disc with { SpeedLimit = SpeedLimit };
                _subchannelTask = ReadSubchannelAsync(disc, ct);
                await _subchannelTask;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is RipException && e.Message.StartsWith(DriveOffsets.NotMeasured, StringComparison.Ordinal))
        {
            // DriveOffsets.Advice: not a failure but the one-time setup of a new drive.
            // A measurement already under way keeps what its CDs found.
            Reset(_offsetCds > 0 ? "One more CD, please" : "New drive: one-time setup");
            _vm.NeedsOffset = true;
            _vm.ErrorText = "";
            _vm.DriveText = CdNotMeasured;
            _vm.OffsetIntro =
                "Before its first rip, Deadwax measures this drive's read offset. Every CD drive hands back the audio a few samples "
                + "early or late, always by the same amount, and the amount differs from one model to the next. Correcting it makes "
                + "your rips the same, bit for bit, as everyone else's, which is what lets AccurateRip confirm them.\n\n"
                + "How it is measured: Deadwax reads one track of the CD in the drive and slides it across every possible offset, "
                + "checking each against the rips other people have submitted to AccurateRip. The offset where they match is this "
                + "drive's. A disc that was pressed more than once can match at more than one offset; then each further CD rules some "
                + "out, until one is left. Usually two or three CDs do it, fewer if they come from different labels.\n\n"
                + "It takes a minute or two, it is done once, and it is remembered for this drive. Each drive you use gets its own.";
            _vm.OffsetText = _offsetCds > 0 ? "A CD is in. Measuring carries on when you press Measure." : "A CD is in. Measure when you're ready.";
            _setupHadCd = true;
        }
        catch (Exception e)
        {
            Reset("The disc could not be read.");
            _vm.ErrorText = e.Message;
        }
    }

    private int? SpeedLimit => _vm.DriveSpeed.Limit;

    // ---- preferences ------------------------------------------------------

    private PrefsWindow? _prefs;

    /// One instance, brought forward if open, as AlbumWall's.
    private void OnPreferences(object? sender, RoutedEventArgs e)
    {
        if (_prefs is null)
        {
            _prefs = new PrefsWindow(this, _vm, _settings);
            _prefs.Closed += (_, _) => _prefs = null;
            if (OperatingSystem.IsMacOS()) NativeMenu.SetMenu(_prefs, MenuBar());
            _prefs.Show(this);
        }
        _prefs.Activate();
    }

    internal DriveIdentity? CurrentDrive => _disc?.Identity;
    internal int? CurrentOffset => _disc?.Offset;
    /// The last answer from GitHub, if any; the About tab reads and refreshes it.
    internal UpdateInfo? LatestUpdate { get => _update; set => _update = value; }
    /// The previous update was staged but could not replace the program.
    internal bool LastUpdateFailed { get; private set; }

    /// The dot on the gear: a newer version exists (or the last update failed).
    /// As AlbumWall's. Nothing is downloaded until asked for on the About tab.
    internal void ShowUpdateDot(bool on)
    {
        UpdateDot.IsVisible = on;
        ToolTip.SetTip(SettingsButton, on ? "Preferences \u2014 a newer version is available" : "Preferences");
        _prefs?.FillUpdate();
    }

    // ---- read offset ------------------------------------------------------

    /// What earlier discs allowed, while he swaps in another (OffsetFinder:
    /// one disc often matches several pressings' shifts as well as the drive).
    private OffsetSearch? _offsetSoFar;
    private int _offsetCds;   // CDs measured in this round, for the summary
    private const string NextCd = "Take this CD out and put in a different well-known one. Measuring carries on by itself when it is in.";

    private async void OnMeasureOffset(object? sender, RoutedEventArgs e) => await MeasureOffsetAsync();

    private void Summary(string line) => _vm.OffsetProgress = _vm.OffsetProgress.Length == 0 ? line : _vm.OffsetProgress + "\n" + line;

    private static string Offsets(OffsetSearch s) => s.Candidates.Count == 1
        ? $"offset {s.Candidates[0].Offset:+0;-0;0}"
        : $"{s.Candidates.Count} offsets ({string.Join(", ", s.Candidates.Select(c => c.Offset.ToString("+0;-0;0")))})";

    internal async Task MeasureOffsetAsync()
    {
        if (_vm.MeasuringOffset) return;
        _vm.MeasuringOffset = true;
        _vm.ErrorText = "";
        _vm.StatusText = "Measuring this drive's read offset…";
        try
        {
            // The step being done stays on screen; the read's progress goes under it.
            _vm.OffsetReading = false;
            var say = new Progress<string>(m => { _vm.OffsetText = m; _vm.OffsetReading = m.StartsWith("Reading", StringComparison.Ordinal); _vm.OffsetPercent = 0; });
            var read = new Progress<(int, int)>(p => _vm.OffsetPercent = p.Item1 * 100.0 / Math.Max(p.Item2, 1));
            var search = await Task.Run(() => OffsetFinder.FindAsync(null,
                m => ((IProgress<string>)say).Report(m), (d, t) => ((IProgress<(int, int)>)read).Report((d, t))));
            _offsetCds++;
            if (search.Candidates.Count == 0)
            {
                Summary($"CD {_offsetCds}: not in AccurateRip well enough to measure from (no offset matched on track {search.Track}).");
                _vm.StatusText = "Try another CD";
                _vm.OffsetText = "A big-selling album works best. " + NextCd;
                return;
            }
            var combined = _offsetSoFar?.Intersect(search) ?? search;
            if (combined.Candidates.Count == 0)
            {
                _offsetSoFar = null;
                Summary($"CD {_offsetCds}: fits {Offsets(search)}, none of which the CD before allowed; starting over from this one.");
                _offsetSoFar = search;
                _vm.StatusText = "One more CD, please";
                _vm.OffsetText = NextCd;
                return;
            }
            if (combined.Best is { } best && search.Drive is { } drive)
            {
                DriveOffsets.Write(drive.Vendor, drive.Model, drive.Revision, best.Offset);
                _offsetSoFar = null;
                _offsetCds = 0;
                _vm.OffsetProgress = "";
                _vm.OffsetText = $"Read offset {best.Offset:+0;-0;0}, agreed by {best.Confidence} rips of track {best.Track}. Saved for {DriveOffsets.Key(drive.Vendor, drive.Model, drive.Revision)}.";
                _vm.Log.Add($"Read offset measured: {best.Offset:+0;-0;0} for {DriveOffsets.Key(drive.Vendor, drive.Model, drive.Revision)} (agreed by {best.Confidence} rips of track {best.Track}).");
                _vm.NeedsOffset = false;
                await ScanAsync();
                return;
            }
            var noNews = _offsetSoFar is not null && combined.Candidates.Count == _offsetSoFar.Candidates.Count;
            Summary(_offsetSoFar is null
                ? $"CD {_offsetCds}: fits {Offsets(search)}, because its pressings differ by a few samples."
                : noNews
                    ? $"CD {_offsetCds}: fits the same {Offsets(combined)} as before, so it could not narrow them down."
                    : $"CD {_offsetCds}: fits {Offsets(search)}; together with the CDs before, {Offsets(combined)} are left.");
            _offsetSoFar = combined;
            _vm.StatusText = "One more CD, please";
            _vm.OffsetText = noNews
                // Discs of one box set or label tend to share the same other pressings.
                ? "Discs from the same box set or label often have the same other pressings. A CD from a different label is more likely to narrow it down. " + NextCd
                : $"{_offsetSoFar!.Candidates.Count} are still possible; another CD narrows them down. " + NextCd;
        }
        catch (Exception ex) when (ex is RipException or DriveException or HttpRequestException)
        {
            _vm.StatusText = "Measuring did not work this time";
            _vm.OffsetText = ex.Message;
        }
        finally
        {
            _vm.MeasuringOffset = false;
        }
    }

    // ---- updates ----------------------------------------------------------

    private UpdateInfo? _update;

    /// A few seconds after launch, so the disc comes first. Only a published
    /// single-file copy is offered anything; a development build is not. All
    /// this ever does is light the dot on the gear (AlbumWall's rule).
    private async Task CheckForUpdateAsync()
    {
        LastUpdateFailed = UpdateService.ConsumeUpdateFailed();
        if (LastUpdateFailed) ShowUpdateDot(true);
        if (!UpdateService.CanUpdate || !_settings.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(7));
        var info = await UpdateService.CheckAsync(_settings.EdgeChannel);
        _update = info;
        ShowUpdateDot(info.UpdateAvailable || LastUpdateFailed);
    }

    /// Fetches the known newer release, checks it, and restarts into it. Called
    /// from the About tab's button. Throws with a plain sentence when it cannot.
    internal async Task ApplyUpdateAsync(UpdateInfo info, IProgress<double> progress)
    {
        if (_vm.IsRipping)
            throw new InvalidOperationException("Finish or stop the rip first; the update restarts Deadwax.");
        var staged = await UpdateService.DownloadAndStageAsync(info, progress);
        UpdateService.ApplyAndRestart(staged);
        Close();
    }

    /// A speed was chosen. Saved for next time, and sent to the drive now if a
    /// disc is in; the rip sets it again before reading. A background scan
    /// under way is NOT restarted: measured 2026-10-08, the BDR-209D runs
    /// cdrdao's subchannel scan at its own speed whatever cap is set (mid-scan,
    /// at load, once idle: 100-113 s every time, against 3-6x for the rip's
    /// audio reads), so a restart only cost the scan again. His view: the
    /// full-speed scan is how a wobbly disc is heard in the first place.
    private void OnSpeedChanged()
    {
        _settings.SlowSpin = SpeedLimit is not null;
        if (SpeedLimit is { } chosen) _settings.SlowSpinSpeed = chosen;
        _settings.Save();
        var device = _disc?.Device;
        if (device is null) return;
        var limit = SpeedLimit;
        _ = Task.Run(() =>
        {
            try { CdDrive.LimitSpeed(device, limit); }
            catch (DriveException e) { Dispatcher.UIThread.Post(() => _vm.ErrorText = e.Message); }
        });
    }

    private CancellationTokenSource? _subchannel;   // the background pass, while it runs
    private Task? _subchannelTask;

    private async Task ReadSubchannelAsync(PreparedDisc disc, CancellationToken ct)
    {
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _subchannel = pass;
        _vm.SubchannelText = disc.SpeedLimit is { } s
            ? $"Reading ISRCs, CD-Text and gaps in the background at {s}x; several minutes. Choose meanwhile."
            : "Reading ISRCs, CD-Text and gaps in the background. Choose meanwhile.";
        try
        {
            var full = await Task.Run(() => RipSession.ReadSubchannelAsync(disc, pass.Token), pass.Token);
            if (_disc != disc) return;
            _disc = full;
            _vm.SubchannelText = "";
            _vm.GapText = full.PreTrackGap > 0 ? $"{full.PreTrackGap} sectors of gap" : "None";
            _vm.CatalogText = full.Catalog ?? "None";
            _vm.DiscTypeText = full.PreEmphasis ? "Pressed CD, pre-emphasis" : "Pressed CD";
            foreach (var r in _vm.Releases) r.BarcodeMatch = r.Candidate.BarcodeMatches(full.Catalog);
            UpdateOutputPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _vm.SubchannelText = "";
            _vm.ErrorText = "Reading the subchannel failed: " + e.Message;
        }
        finally
        {
            if (_subchannel == pass) _subchannel = null;
        }
    }

    private void ShowDisc(PreparedDisc disc)
    {
        var identity = disc.Identity;
        var model = identity is null ? disc.Device : string.Join(' ', identity.Model.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        _vm.DriveText = $"{model} · offset {disc.Offset:+0;-0;0}";
        var lengthSeconds = (disc.Toc.LeadoutLsn - disc.Toc.Tracks[0].StartLsn) / Toc.SectorsPerSecond;
        _vm.TracksText = $"{disc.AudioTracks} · {lengthSeconds / 60}:{lengthSeconds % 60:D2}";
        _vm.DiscTypeText = "Pressed CD";
        _vm.GapText = "Reading...";
        _vm.CatalogText = "Reading...";
        _vm.DiscIdText = disc.DiscId;
        _vm.AccurateRipText = "Checked when ripping";

        _vm.Releases.Clear();
        foreach (var c in disc.Candidates) _vm.Releases.Add(Row(c));
        _vm.ReleasesChanged();
        ShowAttach(disc, null);

        // The description card: the tracks from the TOC, filled from what he
        // typed for this disc before, open by itself when MusicBrainz has nothing.
        var saved = DiscDescription.Load(disc.DiscId);
        _vm.DescribeArtist = saved?.Artist ?? "";
        _vm.DescribeAlbum = saved?.Album ?? "";
        _vm.DescribeYear = saved?.Year ?? "";
        _vm.DescribeNote = saved is null ? "" : "Filled in from last time.";
        _vm.DescribeTracks.Clear();
        var i = 0;
        foreach (var t in disc.Toc.IdTracks.Where(t => t.IsAudio))
        {
            var s = (disc.Toc.EndLsn(t) - t.StartLsn) / Toc.SectorsPerSecond;
            var title = saved is not null && i < saved.Titles.Count ? saved.Titles[i] : $"Track {t.Number}";
            _vm.DescribeTracks.Add(new DescribeTrack { Number = t.Number, Length = $"{s / 60}:{s % 60:D2}", Title = title });
            i++;
        }
        _vm.DescribeOpen = disc.Candidates.Count == 0;
    }

    private async void OnChooseCover(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Cover image",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("JPEG image") { Patterns = ["*.jpg", "*.jpeg"] }],
        });
        if (picked.Count == 0) return;
        try
        {
            await using var stream = await picked[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) { _vm.DescribeNote = "That is not a JPEG file."; return; }
            _coverBytes = bytes;
            _vm.Cover = new Bitmap(new MemoryStream(bytes));
            _vm.DescribeNote = $"Cover: {picked[0].Name}";
        }
        catch (Exception ex)
        {
            _vm.DescribeNote = "Could not read that file: " + ex.Message;
        }
    }

    private void OnUseDescription(object? sender, RoutedEventArgs e)
    {
        if (_disc is null) return;
        var description = new DiscDescription(_vm.DescribeArtist.Trim(), _vm.DescribeAlbum.Trim(), _vm.DescribeYear.Trim(),
            _vm.DescribeTracks.Select(t => t.Title.Trim()).ToList());
        if (description.Problem() is { } problem) { _vm.DescribeNote = problem; return; }
        var version = ++_planVersion;
        try
        {
            var plan = RipSession.PlanSelfDescribed(_disc, description);
            description.Save(_disc.DiscId);
            foreach (var r in _vm.Releases) r.IsSelected = false;
            _plan?.Dispose();
            _plan = plan;
            _vm.OriginalYear = _vm.EditionYear = description.Year;
            _vm.HasYearChoice = false;
            _vm.UseOriginal = true;
            _vm.IsSetDisc = false;
            _vm.SetMismatch = "";
            _vm.NoteText = "";
            _vm.KeepNote = false;
            _vm.YearNote = $"{description.Year}, as you described it.";
            _vm.DescribeNote = "Using this description." + (_coverBytes is null ? " No cover chosen." : "");
            _vm.ErrorText = "";
            UpdateOutputPreview();
        }
        catch (RipException ex)
        {
            if (version == _planVersion) _vm.DescribeNote = ex.Message;
        }
    }

    private static ReleaseRow Row(ReleaseCandidate c)
    {
        var detail = string.Join(" · ", new[]
        {
            c.Date is { Length: >= 4 } d ? d[..4] : "no date",
            c.Country,
            c.MediaCount > 1 ? $"disc {c.DiscPosition} of {c.MediaCount}" : "CD",
        }.Where(x => !string.IsNullOrEmpty(x)));
        var printed = string.Join(" · ", new[] { c.Label, c.CatalogNumber, c.Barcode is { Length: > 0 } b ? "barcode " + b : null }
            .Where(x => !string.IsNullOrEmpty(x)));
        return new ReleaseRow
        {
            Candidate = c,
            Title = c.MediaCount > 1 && c.MediumTitle is not null ? $"{c.Title} · {c.MediumTitle}" : c.Title,
            Note = c.Disambiguation is { } note ? $"({note})" : null,
            Detail = detail,
            Printed = printed,
        };
    }

    private async void OnReleaseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReleaseRow row }) await ChooseAsync(row);
    }

    private async void OnReleaseLinkKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await UseReleaseLinkAsync();
    }

    private async void OnUseReleaseLink(object? sender, RoutedEventArgs e) => await UseReleaseLinkAsync();

    /// The set card's "Use the same edition": the release the other disc came
    /// from, chosen as if its link had been pasted.
    private async void OnUseSiblingEdition(object? sender, RoutedEventArgs e)
    {
        if (_vm.SetMismatchRelease is not { } id) return;
        ReleaseLink.Text = "https://musicbrainz.org/release/" + id;
        await UseReleaseLinkAsync();
    }

    /// A release he names by link, for a disc whose disc ID isn't attached to
    /// it: the disc is found on it by track count and lengths, then it joins
    /// the list, picked, like any other.
    private async Task UseReleaseLinkAsync()
    {
        if (_disc is null) return;
        var id = ReleaseChoice.ReleaseIdFrom(ReleaseLink.Text ?? "");
        if (id is null)
        {
            _vm.ErrorText = "That isn't a MusicBrainz release link. Open the edition itself (musicbrainz.org/release/…), not the release group.";
            return;
        }
        var existing = _vm.Releases.FirstOrDefault(r => r.Id == id);
        if (existing is not null) { await ChooseAsync(existing); return; }

        _vm.ErrorText = "";
        var disc = _disc;
        try
        {
            var candidate = await Task.Run(async () =>
            {
                using var mb = new MusicBrainzClient(MusicBrainzClient.DefaultCacheDir);
                using var doc = await mb.GetReleaseAsync(id, fresh: true) ?? throw new MusicBrainzException("MusicBrainz has no release with that ID.");
                var root = doc.RootElement;
                var medium = ReleaseTags.FindMedium(root, disc.DiscId) ?? ReleaseTags.MediumByTracks(root, disc.Toc);
                return ReleaseChoice.FromRelease(root, medium);
            });
            if (_disc != disc) return;
            var row = Row(candidate);
            row.BarcodeMatch = candidate.BarcodeMatches(disc.Catalog);
            _vm.Releases.Add(row);
            _vm.ReleasesChanged();
            ReleaseLink.Text = "";
            await ChooseAsync(row);
        }
        catch (Exception ex)
        {
            if (_disc == disc) _vm.ErrorText = ex.Message;
        }
    }

    /// A release picked: plan it, show its cover, offer the years.
    private async Task ChooseAsync(ReleaseRow row)
    {
        if (_disc is null) return;
        foreach (var r in _vm.Releases) r.IsSelected = r == row;
        var version = ++_planVersion;
        _vm.CanRip = false;
        _vm.ErrorText = "";
        try
        {
            var disc = _disc;
            var plan = await Task.Run(() => RipSession.PlanAsync(disc, row.Id, Library));
            if (version != _planVersion) { plan.Dispose(); return; }
            _plan?.Dispose();
            _plan = plan;

            _vm.OriginalYear = plan.OriginalYear ?? plan.DefaultYear;
            _vm.EditionYear = plan.EditionYear ?? plan.DefaultYear;
            _vm.HasYearChoice = _vm.OriginalYear != _vm.EditionYear;
            _vm.UseOriginal = plan.DefaultYear == _vm.OriginalYear;
            _vm.IsSetDisc = plan.MediaCount > 1;
            _vm.SetChoice = _vm.IsSetDisc && plan.ReleaseId is { } setId && _settings.SetChoices.TryGetValue(setId, out var remembered)
                ? remembered : null;
            _vm.SetChoiceNote = _vm.SetChoice is null
                ? "Pick one before ripping. Deadwax remembers it for the other discs of this set."
                : "Remembered from an earlier disc of this set. Change it here if you like.";
            ShowAttach(disc, plan);
            _vm.NoteText = plan.Disambiguation ?? "";
            _vm.KeepNote = false;

            var siblings = _vm.IsSetDisc ? await Task.Run(() => SetSiblings.Find(Library, plan)) : [];
            if (version != _planVersion) return;
            var sibling = siblings.FirstOrDefault();
            _vm.SetMismatchRelease = sibling?.ReleaseId;
            _vm.SetMismatch = sibling is null ? ""
                : $"Disc {sibling.Disc ?? "?"} of this set is already in your library from a different edition ({sibling.Folder}). "
                  + "Kept together, discs from two editions show as two separate albums in most players.";
            _vm.YearNote = !_vm.HasYearChoice
                ? $"{plan.DefaultYear}: this edition and the album's first release agree."
                : plan.IsCompilation
                    ? "A compilation: the year of this edition is offered first, as the library keeps compilations."
                    : "An album: its original year is offered first, as the library keeps albums, even on a reissue.";
            UpdateOutputPreview();

            var cover = await RipSession.FrontCoverAsync(plan);
            if (version != _planVersion) return;
            _coverBytes = cover;
            _vm.Cover = cover is null ? null : new Bitmap(new MemoryStream(cover));
        }
        catch (Exception ex)
        {
            if (version == _planVersion) _vm.ErrorText = ex.Message;
        }
    }

    private string ChosenYear => _vm.HasYearChoice && !_vm.UseOriginal ? _vm.EditionYear : _vm.UseOriginal ? _vm.OriginalYear : _vm.EditionYear;

    /// Preferences > Filing changed: new rips follow it, and the preview shows it.
    internal void FilingChanged()
    {
        Filing.Current = _settings.ToFiling();
        UpdateOutputPreview();
    }

    private void UpdateOutputPreview()
    {
        if (_plan is null) return;
        _plan.KeepDisambiguation = _vm.KeepNote;
        var dir = _plan.AlbumDirectory(Library, ChosenYear);
        _vm.OutputFolder = dir.Replace(Home, "~") + "/";
        var first = _plan.Tracks.FirstOrDefault();
        var filing = Filing.Current;
        var extras = string.Join("  ", new[]
        {
            filing.Cue ? ".cue" : null, ".log", filing.M3u ? ".m3u" : null, ".toc", "cover.jpg",
            filing.BackCover && (_settings.PostRip ?? true) && !(_vm.IsSetDisc && _vm.Unbox) ? "back.jpg" : null,
        }.Where(x => x is not null));
        _vm.OutputFiles = first is null ? "" :
            $"  {first.FileName}\n  … {_plan.Tracks.Count - 1} more tracks{(filing.EmbedCover ? ", the cover embedded in each" : "")}\n  {extras}" +
            (_vm.IsSetDisc && _vm.Unbox ? "\n  then filed as its own album" : "");
        var exists = Directory.Exists(dir);
        if (_libraryMatch is not null)
            _vm.ErrorText = $"Already in your library: {Path.GetRelativePath(Library, _libraryMatch)}";
        else
            _vm.ErrorText = exists ? "That folder is already in the library. Deadwax never writes over an album." : "";
        _vm.CanRip = !exists && _libraryMatch is null && _disc?.Cdrdao is not null
                     && (!_vm.IsSetDisc || _vm.SetChoice is not null);
    }

    private async void OnRescan(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async void OnEject(object? sender, RoutedEventArgs e)
    {
        // During the one-time setup Eject only opens the tray: the setup, and
        // what the CDs so far have found, stay (2026-10-20: a Reset here ended
        // the setup, and the next CD started the measurement from nothing).
        if (_vm.NeedsOffset)
        {
            if (!_vm.MeasuringOffset) await EjectAsync();
            return;
        }
        _work?.Cancel();
        Reset("Put a CD in the drive.");
        await EjectAsync();
    }

    private static Task EjectAsync() => Task.Run(() => CdDrive.Eject(CdDrive.DefaultDevice));

    private void ClearDisc()
    {
        _plan?.Dispose();
        _plan = null;
        _disc = null;
        _planVersion++;
        _vm.Cover = null;
        _vm.Releases.Clear();
        _vm.ReleasesChanged();
        _vm.AttachText = "";
        _vm.AttachUrl = null;
        _vm.CanRip = false;
        _vm.HasYearChoice = false;
        _vm.IsSetDisc = false;
        _vm.SetMismatch = "";
        _vm.NoteText = "";
        _vm.SubchannelText = "";
        _coverBytes = null;
        _libraryMatch = null;
    }

    private void Reset(string status)
    {
        ClearDisc();
        _vm.Stage = Stage.Waiting;
        _vm.StatusText = status;
        _vm.NeedsOffset = false;
    }

    // ------------------------------------------------------------ the rip

    private async void OnRip(object? sender, RoutedEventArgs e)
    {
        if (_disc is null || _plan is null) return;
        var disc = _disc with { SpeedLimit = SpeedLimit };   // the speed as chosen when Rip is pressed
        var plan = _plan;
        var year = ChosenYear;
        var unbox = _vm.IsSetDisc && _vm.Unbox;
        if (_vm.IsSetDisc && plan.ReleaseId is { } setId)
        {
            _settings.SetChoices[setId] = unbox;
            _settings.Save();
        }
        var cover = _coverBytes;
        var albumDir = plan.AlbumDirectory(Library, year);

        _vm.AlbumTitle = plan.DiscTitle;
        _vm.AlbumLine = $"{plan.AlbumArtist} · {year}";
        _vm.Tracks.Clear();
        foreach (var t in plan.Tracks)
        {
            var s = t.Sectors / Toc.SectorsPerSecond;
            _vm.Tracks.Add(new TrackRow { Number = t.Number, Title = t.Title, Length = $"{s / 60}:{s % 60:D2}" });
        }
        var cells = (int)Math.Ceiling(disc.Toc.LeadoutLsn / (double)Observer.SectorsPerCell);
        _vm.Map.Clear();
        for (var i = 0; i < cells; i++) _vm.Map.Add(new MapCell());
        _vm.Log.Clear();
        _vm.Overall = 0;
        _vm.NowText = "Starting: AccurateRip, the front cover, then the drive...";
        _vm.Stage = Stage.Ripping;
        _watch.Stop();

        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        var observer = new Observer(_vm, disc, _settings.AlwaysTwoPasses);
        try
        {
            var result = await Task.Run(() => RipSession.RipAsync(disc, plan, year, Library, observer,
                oneReadConfidence: _settings.AlwaysTwoPasses ? null : OneRead.DefaultConfidence, cover: cover, ct: ct), ct);
            observer.Finish();
            var steps = new List<StepRow>();
            var ok = result.Tracks.Count(t => t.CopyOk);
            var once = result.Tracks.Count(t => t.Passes == 1);
            steps.Add(new StepRow
            {
                Name = "Read", Ok = result.AllOk,
                Detail = !result.AllOk ? $"{result.Tracks.Count - ok} track(s) never read the same twice"
                    : once == ok ? $"{ok} tracks, each proven by AccurateRip on one read"
                    : once == 0 ? $"{ok} tracks, every one read twice alike"
                    : $"{once} proven by AccurateRip on one read, {ok - once} read twice alike",
            });
            var accurate = result.Tracks.Where(t => t.AccurateRip.IsAccurate).ToList();
            steps.Add(new StepRow
            {
                Name = "AccurateRip", Ok = accurate.Count == result.Tracks.Count,
                Detail = accurate.Count == 0 ? "No match (the disc may not be in the database)"
                    : $"{accurate.Count} of {result.Tracks.Count} accurate, {accurate.Min(t => t.AccurateRip.Confidence)}–{accurate.Max(t => t.AccurateRip.Confidence)} matching rips",
            });

            var dir = result.AlbumDirectory;
            if (result.AllOk && (_settings.PostRip ?? true))
            {
                _vm.NowText = unbox ? "Into the library: filing it as its own album, then the back cover..." : "Into the library: the back cover...";
                var after = await Task.Run(() => PostRip.RunAsync(dir, unbox, observer.Say, ct), ct);
                if (after.AlbumDirectory != dir)
                    steps.Add(new StepRow { Name = "Filed as its own album", Ok = true, Detail = Path.GetFileName(after.AlbumDirectory) });
                dir = after.AlbumDirectory;
                steps.Add(new StepRow { Name = "Front cover", Ok = File.Exists(Path.Combine(dir, "cover.jpg")), Detail = File.Exists(Path.Combine(dir, "cover.jpg")) ? "cover.jpg" : "None in the Cover Art Archive" });
                if (Filing.Current.BackCover)
                    steps.Add(new StepRow { Name = "Back cover", Ok = File.Exists(Path.Combine(dir, "back.jpg")), Detail = File.Exists(Path.Combine(dir, "back.jpg")) ? "back.jpg" : "None in the Cover Art Archive" });
                foreach (var note in after.Notes) _vm.Log.Add("note: " + note);
            }

            // This album against the choices in Preferences > Filing.
            var filed = await Task.Run(() => FiledCheck.Run(dir, Filing.Current), ct);
            foreach (var issue in filed) _vm.Log.Add("filed: " + issue);
            steps.Add(new StepRow
            {
                Name = "Filed correctly", Ok = filed.Count == 0,
                Detail = filed.Count == 0 ? "tags, file names and playlists check out"
                       : filed.Count == 1 ? filed[0] : $"{filed[0]} (and {filed.Count - 1} more; see the engine log)",
            });

            _vm.AllGood = steps.All(s => s.Ok);
            _vm.Verdict = result.AllOk
                ? accurate.Count == result.Tracks.Count ? $"All {result.Tracks.Count} tracks accurately ripped" : $"All {result.Tracks.Count} tracks ripped"
                : "Ripped, with problems";
            _vm.VerdictDetail = result.AllOk
                ? once == 0 ? "Every track read twice with the same result."
                    : once == result.Tracks.Count ? "Every track's one read matched other people's rips in AccurateRip."
                    : "Every track proven: its one read matched AccurateRip, or two reads agreed."
                : "Some tracks never read the same way twice. Clean the disc and rip it again.";
            _vm.Steps.Clear();
            foreach (var s in steps) _vm.Steps.Add(s);
            _vm.AlbumDirectory = dir.Replace(Home, "~") + "/";
            _vm.Files.Clear();
            var flacs = Directory.EnumerateFiles(dir, "*.flac").Count();
            _vm.Files.Add(plan.IsSelfDescribed ? $"  {flacs} × FLAC, tagged from your description" : $"  {flacs} × FLAC, tagged, with MusicBrainz IDs");
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".flac")).Select(Path.GetFileName).Order())
                _vm.Files.Add("  " + f);
            _lastAlbum = dir;
            _intakeEmptied = false;
            _vm.Stage = Stage.Intake;
            // As whipper did; the Intake screen's "Eject, next disc" still works for the rest.
            if (_settings.EjectAfterRip) await EjectAsync();
        }
        catch (OperationCanceledException)
        {
            // Back to the Disc screen with the disc still in, as after an error:
            // a Reset here had the watcher read the same disc again at once,
            // and that scan cleared the offer below (2026-10-21).
            _vm.Stage = Stage.Disc;
            _vm.ErrorText = "Stopped. Rip it again, or eject it.";
            OfferPartial(albumDir);
        }
        catch (Exception ex)
        {
            _vm.Stage = Stage.Disc;
            _vm.ErrorText = ex.Message;
            OfferPartial(albumDir);
        }
        finally
        {
            _watch.Start();
        }
    }

    private string? _lastAlbum;
    private bool _intakeEmptied;   // the drive has been seen empty since the summary came up

    /// A rip that stopped partway leaves its tracks in the library, where
    /// AlbumWall already shows them; say so, and offer to take them out.
    private void OfferPartial(string albumDir)
    {
        if (Directory.Exists(albumDir)) _vm.PartialAlbum = albumDir;
    }

    private void OnRemovePartial(object? sender, RoutedEventArgs e)
    {
        var dir = _vm.PartialAlbum;
        // Only ever a folder this window was ripping into, inside the library.
        if (dir.Length == 0 || !Path.GetFullPath(dir).StartsWith(Library + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
            var artistDir = Path.GetDirectoryName(dir)!;
            if (Directory.Exists(artistDir) && !Directory.EnumerateFileSystemEntries(artistDir).Any()) Directory.Delete(artistDir);
            _vm.PartialAlbum = "";
        }
        catch (Exception ex)
        {
            _vm.ErrorText = "Could not remove it: " + ex.Message;
        }
    }

    /// Stops the rip between reads (the reader checks between them; a read is
    /// never cut short, which the BDR-209D takes badly). Before 2026-10-21 the
    /// only way out of a rip was quitting. Asked first, with "Keep ripping" as
    /// the default for Enter and Esc: a stray click costs the minutes read so far.
    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        var stop = await new ConfirmWindow("Stop", "Stop this rip?",
            affirmative: "Stop", negative: "Keep ripping",
            detail: "The tracks read so far stay in your library until you remove them.",
            negativeIsDefault: true).ShowDialog<bool>(this);
        if (!stop || !_vm.IsRipping) return;   // the rip may have finished while it asked
        _vm.NowText = "Stopping after this read...";
        _work?.Cancel();
    }

    private void OnToggleLog(object? sender, RoutedEventArgs e) => _vm.ShowLog = !_vm.ShowLog;

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_lastAlbum is not null) Open(_lastAlbum);
    }

    private void OnViewLog(object? sender, RoutedEventArgs e)
    {
        var log = _lastAlbum is null ? null : Directory.EnumerateFiles(_lastAlbum, "*.log").FirstOrDefault();
        if (log is not null) Open(log);
    }

    /// The attach offer: for a chosen release that does not carry this disc's
    /// ID (matched by track lengths), MusicBrainz's attach page with that
    /// release already found; for a disc no release carries, the page's search. Nothing when the ID is there.
    private void ShowAttach(PreparedDisc disc, ReleasePlan? plan)
    {
        if (plan is { ReleaseId: not null, MatchedByTracks: true })
        {
            _vm.AttachUrl = DiscIds.MusicBrainzAttachUrl(disc.Toc, plan.ReleaseId);
            _vm.AttachText = "This disc's ID is not on this release at MusicBrainz yet, so the next copy of it won't be found by itself. "
                             + "The button opens MusicBrainz with this release already found: pick its disc and press Attach CD TOC (it asks you to log in).";
        }
        else if (plan is null && disc.Candidates.Count == 0)
        {
            _vm.AttachUrl = DiscIds.MusicBrainzAttachUrl(disc.Toc);
            _vm.AttachText = "Once this disc's release is on MusicBrainz, attaching the disc there lets every copy of it be found by itself.";
        }
        else
        {
            _vm.AttachUrl = null;
            _vm.AttachText = "";
        }
    }

    private void OnAttachDisc(object? sender, RoutedEventArgs e)
    {
        if (_vm.AttachUrl is { } url) Open(url);
    }

    /// A folder, a file or a web address, in whatever the desktop opens it
    /// with, through Avalonia's launcher (xdg-open on Linux, the shell on
    /// Windows, Launch Services on macOS).
    private void Open(string path)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null) return;
            if (Directory.Exists(path)) _ = launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
            else if (File.Exists(path)) _ = launcher.LaunchFileInfoAsync(new FileInfo(path));
            else if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) _ = launcher.LaunchUriAsync(uri);
        }
        catch { }
    }

    private async void OnNextDisc(object? sender, RoutedEventArgs e)
    {
        Reset("Put the next CD in the drive.");
        await EjectAsync();
    }

    /// The rip's reports, carried to the window. Progress arrives per sector, so
    /// it is thinned to whole percent before it crosses to the UI thread.
    private sealed class Observer(MainViewModel vm, PreparedDisc disc, bool alwaysTwice) : IRipObserver
    {
        /// The rip's passes, as the window names them: the first read, then
        /// any later one (RipSession's "test" and "copy"). With one read the
        /// norm, a first read is not a "test" (2026-10-20).
        private static string Label(string pass) => pass == "test" ? "reading" : "reading again";
        private string Now(string pass) => pass == "test" ? "reading"
            : alwaysTwice ? "reading again" : "reading again, to confirm it";

        public const int SectorsPerCell = 150;   // two seconds

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Stopwatch _pass = new();
        private readonly Dictionary<int, IBrush> _worst = [];
        private readonly long _totalWork = 2L * disc.Toc.Tracks.Where(t => t.IsAudio).Sum(t => (long)(disc.Toc.EndLsn(t) - t.StartLsn));
        private long _workDone;
        private int _lastPct = -1;

        public void Say(string message) => Dispatcher.UIThread.Post(() =>
        {
            vm.Log.Add(message);
            if (vm.Log.Count > 400) vm.Log.RemoveAt(0);
            // A track needing a third pass is trouble too (RipSession says "pass N differs").
            if (message.Contains(" differs from pass ", StringComparison.Ordinal)) ShowTrouble();
        });

        /// Trouble opens the engine log, once a rip (his idea, 2026-10-24: when
        /// a read goes wrong, show what is happening there). Closed again by
        /// hand, it stays closed.
        private bool _troubleShown;
        private void ShowTrouble()
        {
            if (_troubleShown) return;
            _troubleShown = true;
            vm.ShowLog = true;
        }

        public void TrackStarted(int track) => Dispatcher.UIThread.Post(() =>
        {
            var row = vm.Tracks.FirstOrDefault(t => t.Number == track);
            if (row is null) return;
            row.Active = true;
            row.Background = Tone.ActiveRow;
            row.Foreground = Tone.Text;
            row.Status = "";
            row.Detail = Label("test");
        });

        public void Progress(int track, string pass, int done, int total)
        {
            if (done == 1) _pass.Restart();
            var pct = done * 100 / total;
            if (pct == _lastPct && done != total) return;
            _lastPct = pct;
            var t = disc.Toc.Track(track);
            var sectors = disc.Toc.EndLsn(t) - t.StartLsn;
            var workNow = _workDone + (long)done * sectors / total;
            var elapsed = _pass.Elapsed.TotalSeconds;
            var speed = elapsed > 0.5 ? done / (double)Toc.SectorsPerSecond / elapsed : 0;
            Dispatcher.UIThread.Post(() =>
            {
                var row = vm.Tracks.FirstOrDefault(r => r.Number == track);
                if (row is not null) { row.Progress = pct; row.Detail = Label(pass); if (speed > 0) row.Speed = $"{speed:0.0}×"; }
                vm.Overall = 100.0 * workNow / _totalWork;
                vm.NowText = $"Track {track} of {vm.Tracks.Count} · {Now(pass)}";
                if (speed > 0) vm.SpeedText = $"{speed:0.0}×";
                vm.ElapsedText = _clock.Elapsed.ToString(@"m\:ss");
                // Both passes sweep: the first read turns cells "reading", a
                // later one "reading again"; damage already painted stays.
                var (from, to) = pass == "test" ? (Tone.Unread, Tone.Reading) : (Tone.Reading, Tone.Copied);
                var last = (t.StartLsn + done) / SectorsPerCell;
                for (var c = t.StartLsn / SectorsPerCell; c <= last && c < vm.Map.Count; c++)
                    if (vm.Map[c].Fill == from) vm.Map[c].Fill = to;
            });
        }

        /// On the reading thread, as paranoia reports it. Only a cell that gets
        /// worse crosses to the UI, so a badly scratched patch costs one post
        /// per cell, not one per sector.
        public void SectorDamaged(int track, string pass, int sector, SectorState state)
        {
            var cell = sector / SectorsPerCell;
            if (cell < 0) return;
            var brush = state == SectorState.Skipped ? Tone.Bad : Tone.Warn;
            if (!Worsen(cell, brush)) return;
            Dispatcher.UIThread.Post(() => { if (cell < vm.Map.Count) vm.Map[cell].Fill = brush; });
        }

        /// The status line says what a long silence at a damaged spot is: which
        /// damaged sector, which try of how many, and how it ended (his ask,
        /// 2026-10-24, after ten minutes at "90%" on Life for Rent).
        public void Retrying(int track, string pass, RetryStatus st)
        {
            var text = st.Result switch
            {
                SectorState.Recovered => $"Track {track} · damaged sector {st.Damaged}: read again and recovered",
                SectorState.Skipped => $"Track {track} · damaged sector {st.Damaged}: could not be read cleanly, kept the drive's best guess",
                _ => $"Track {track} · re-reading damaged sector {st.Damaged} (try {st.Attempt} of {st.Tries})",
            };
            Dispatcher.UIThread.Post(() =>
            {
                vm.NowText = text;
                // The log keeps the story: each damaged sector once when its tries begin, and how it ended.
                if (st.Attempt == 1 && st.Result is null)
                    vm.Log.Add($"Track {track}: damaged sector {st.Damaged} (sector {st.Sector}), reading it again, up to {st.Tries} tries");
                else if (st.Result is { } result)
                    vm.Log.Add($"Track {track}: damaged sector {st.Damaged} {(result == SectorState.Recovered ? "recovered" : "could not be read cleanly; the drive's best guess kept")} after {st.Attempt} tr{(st.Attempt == 1 ? "y" : "ies")}");
                if (vm.Log.Count > 400) vm.Log.RemoveAt(0);
                ShowTrouble();
            });
        }

        /// Records the worst seen in a cell; true if it got worse.
        private bool Worsen(int cell, IBrush brush)
        {
            lock (_worst)
            {
                if (_worst.TryGetValue(cell, out var had) && (had == Tone.Bad || had == brush)) return false;
                _worst[cell] = brush;
                return true;
            }
        }

        public void PassFinished(int track, string pass, ReadReport report)
        {
            var t = disc.Toc.Track(track);
            _workDone += disc.Toc.EndLsn(t) - t.StartLsn;
            _lastPct = -1;
            for (var i = 0; i < report.Sectors.Length; i++)
            {
                var cell = (report.FirstSector + i) / SectorsPerCell;
                var brush = report.Sectors[i] switch
                {
                    SectorState.Skipped => Tone.Bad,
                    SectorState.Recovered => Tone.Warn,
                    _ => null,
                };
                if (brush is null || cell < 0) continue;
                Worsen(cell, brush);
            }
        }

        public void TrackFinished(RippedTrack result)
        {
            // Read once: the copy pass the progress bar counted on never ran.
            if (result.Passes == 1 && result.CopyOk)
            {
                var t1 = disc.Toc.Track(result.Number);
                _workDone += disc.Toc.EndLsn(t1) - t1.StartLsn;
            }
            Dispatcher.UIThread.Post(() => ShowFinished(result));
        }

        private void ShowFinished(RippedTrack result)
        {
            var t = disc.Toc.Track(result.Number);
            lock (_worst)
                for (var c = t.StartLsn / SectorsPerCell; c <= (disc.Toc.EndLsn(t) - 1) / SectorsPerCell && c < vm.Map.Count; c++)
                    vm.Map[c].Fill = _worst.TryGetValue(c, out var b) ? b
                        : !result.CopyOk ? Tone.Bad : result.AccurateRip.IsAccurate ? Tone.MapGood : Tone.MapTwice;

            var row = vm.Tracks.FirstOrDefault(r => r.Number == result.Number);
            if (row is null) return;
            row.Active = false;
            row.Background = Avalonia.Media.Brushes.Transparent;
            row.TestCrc = result.Crc.ToString("X8");
            row.CopyCrc = result.Passes == 1 && result.CopyOk ? "—" : result.Crc.ToString("X8");   // read once: no copy
            if (!result.CopyOk) { row.Status = "Never read alike"; row.StatusBrush = Tone.Bad; row.Detail = $"{result.Passes} passes"; return; }
            if (result.AccurateRip.IsAccurate) { row.Status = "✓ Accurate"; row.StatusBrush = Tone.Good; row.Detail = $"{result.AccurateRip.Confidence} matching rips"; }
            else { row.Status = "Copy OK"; row.StatusBrush = Tone.Good; row.Detail = "no AccurateRip match"; }
            if (result.Passes == 1) row.Detail += " · one read";
            if (result.Passes > 2) row.Detail += $" · {result.Passes} passes";
        }

        public void Finish() => Dispatcher.UIThread.Post(() =>
        {
            vm.Overall = 100;
            vm.ElapsedText = _clock.Elapsed.ToString(@"m\:ss");
        });
    }

    // ------------------------------------------------------------ the frame
    // AlbumWall's title bar and resize band, the same behaviour in both apps.

    private void BuildWindowButtons()
    {
        // macOS draws its own traffic lights over the top bar (UseMacTitleBar), so
        // nothing is drawn here: the left panel only keeps their place clear.
        if (OperatingSystem.IsMacOS())
        {
            LeftButtons.Width = MacTrafficLightsWidth;
            return;
        }

        var (left, right) = WindowButtons.Layout();
        foreach (var k in left) LeftButtons.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        foreach (var k in right) RightButtons.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        // Square buttons (Windows, macOS): flush to each other and to the window's
        // edge, the close button's red filling the corner (the Surface clip rounds
        // it), as Windows' own caption buttons do; AlbumWall's BuildWindowButtons
        // does the same. Round ones (Linux) keep their gaps. -12 undoes the top
        // bar's 12 px side margin on the side the buttons are on.
        if (!OperatingSystem.IsLinux())
        {
            LeftButtons.Spacing = RightButtons.Spacing = 0;
            if (right.Length > 0) RightButtons.Margin = new Thickness(0, 0, -12, 0);
            if (left.Length > 0) LeftButtons.Margin = new Thickness(-12, 0, LeftButtons.Margin.Right, 0);
        }

        void Invoke(WindowButtons.Kind k)
        {
            switch (k)
            {
                case WindowButtons.Kind.Minimize: WindowState = WindowState.Minimized; break;
                case WindowButtons.Kind.Maximize: ToggleMaximized(); break;
                default: Close(); break;
            }
        }
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// Tracks below the current one kept in view while ripping, room allowing:
    /// "it would be nice if the window could autoscroll to keep the current
    /// track in view", then "maybe even a few tracks below the current one"
    /// (2026-10-18). The finished track above stays too when there is room; the
    /// current one always does.
    private const int TracksAhead = 3;

    /// Scrolled by hand this recently, the list is left where it was put: it
    /// does not snatch a table someone is reading.
    private static readonly TimeSpan HandsOffAfterScrolling = TimeSpan.FromSeconds(15);
    private long _trackListScrolledByHand;

    /// The rip table follows the track being read when it has more tracks than
    /// the window has room for.
    private void FollowTheCurrentTrack()
    {
        TrackScroll.AddHandler(PointerWheelChangedEvent,
            (_, _) => _trackListScrolledByHand = Environment.TickCount64,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // A drag on the scroll bar is a hand too.
        TrackScroll.AddHandler(PointerPressedEvent,
            (_, _) => _trackListScrolledByHand = Environment.TickCount64,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        _vm.Tracks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (TrackRow row in e.NewItems)
                row.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == nameof(TrackRow.Active) && row.Active)
                        Dispatcher.UIThread.Post(() => ShowTrack(row), DispatcherPriority.Background);
                };
        };
    }

    private void ShowTrack(TrackRow row)
    {
        if (_trackListScrolledByHand != 0
            && Environment.TickCount64 - _trackListScrolledByHand < HandsOffAfterScrolling.TotalMilliseconds) return;
        var index = _vm.Tracks.IndexOf(row);
        if (index < 0 || TrackList.ContainerFromIndex(index) is not Control container) return;
        if (container.TranslatePoint(default, TrackList) is not { } at) return;

        double h = container.Bounds.Height, top = at.Y, view = TrackScroll.Viewport.Height;
        if (h <= 0 || view <= 0) return;
        var offset = TrackScroll.Offset.Y;
        var wantTop = Math.Max(0, top - h);                                  // the finished track above
        var wantBottom = top + h * (1 + Math.Min(TracksAhead, _vm.Tracks.Count - 1 - index));
        if (wantTop >= offset && wantBottom <= offset + view) return;       // already in view: no jump
        // The view is [offset, offset + view]. Room for everything: move the
        // least that shows the track above through the tracks ahead. Not
        // enough room: the tracks ahead come before the one above, and the
        // current track always shows.
        offset = wantBottom - view <= wantTop
            ? Math.Clamp(offset, wantBottom - view, wantTop)
            : Math.Min(wantBottom - view, top);
        var max = Math.Max(0, TrackScroll.Extent.Height - view);
        TrackScroll.Offset = new Vector(TrackScroll.Offset.X, Math.Clamp(offset, 0, max));
    }

    private const double ResizeBand = 7;
    private StandardCursorType _cursor = StandardCursorType.Arrow;

    /// How much of the top bar's left end the traffic lights take on macOS: the
    /// three buttons and the gap the system leaves around them.
    private const double MacTrafficLightsWidth = 70;

    /// macOS: a REAL title bar, extended under the top bar, instead of none.
    /// AlbumWall's UseMacTitleBar, for the same reasons: with no system
    /// decorations the window is a borderless NSWindow, which has no traffic
    /// lights and no edges to resize by (the first Mac, 2026-10-15: "I cannot
    /// resize the deadwax window at all", "there are no stoplights"). With the
    /// system's title bar macOS draws the lights and resizes at its own edges.
    /// The green button zooms instead of entering full screen, as AlbumWall's
    /// does (MacWindow).
    private void UseMacTitleBar()
    {
        if (!OperatingSystem.IsMacOS()) return;
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        // Meant to centre the traffic lights on the top bar. On the Mac it does
        // not (MacWindow.CenterTrafficLights says how it was found), so
        // CenterTrafficLights below does it, but the hint stays: it is what
        // Avalonia's own layout reads as the title bar's height.
        ExtendClientAreaTitleBarHeightHint = TopBar.Height;
        Opened += (_, _) =>
        {
            var h = TryGetPlatformHandle();
            MacWindow.ZoomInsteadOfFullScreen(h?.Handle ?? IntPtr.Zero, h?.HandleDescriptor);

            var center = CenterTrafficLights();
            Console.WriteLine($"[window] traffic lights centred {center:0.#} pt from the top "
                            + $"(the bar is {TopBar.Height} pt)");
            // Read back, not set: whether AppKit has put them back on its own.
            DispatcherTimer.RunOnce(() =>
            {
                var hh = TryGetPlatformHandle();
                var now = MacWindow.TrafficLightCenter(hh?.Handle ?? IntPtr.Zero, hh?.HandleDescriptor);
                Console.WriteLine($"[window] traffic lights 2 s later: {now:0.#} pt from the top");
            }, TimeSpan.FromSeconds(2));
        };
        // AppKit lays its title bar out again after each of these and puts the
        // lights back where it keeps them, so they are moved again once it has.
        SizeChanged += (_, _) => RecenterTrafficLights();
        Activated += (_, _) => RecenterTrafficLights();
        Deactivated += (_, _) => RecenterTrafficLights();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) RecenterTrafficLights(); };
    }

    private double? CenterTrafficLights()
    {
        var h = TryGetPlatformHandle();
        return MacWindow.CenterTrafficLights(h?.Handle ?? IntPtr.Zero, h?.HandleDescriptor, TopBar.Height);
    }

    /// At once and again once AppKit is done: during a live resize it lays the
    /// title bar out on every frame, and moved only afterwards the lights
    /// jumped about until the drag stopped (AlbumWall, 2026-10-18).
    private void RecenterTrafficLights()
    {
        CenterTrafficLights();
        Dispatcher.UIThread.Post(() => CenterTrafficLights(), DispatcherPriority.Render);
    }

    private WindowEdge? EdgeAt(Point p)
    {
        if (WindowState != WindowState.Normal) return null;
        if (OperatingSystem.IsMacOS()) return null;           // the system's own edges do it
        double w = Bounds.Width, h = Bounds.Height;
        bool l = p.X <= ResizeBand, r = p.X >= w - ResizeBand, t = p.Y <= ResizeBand, b = p.Y >= h - ResizeBand;
        return (l, r, t, b) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }

    private void OnPointerMovedForResize(object? sender, PointerEventArgs e)
    {
        var cursor = EdgeAt(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
            WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
            WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
            WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
            WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
            _ => StandardCursorType.Arrow,
        };
        if (_cursor == cursor) return;
        _cursor = cursor;
        Cursor = new Cursor(cursor);
    }

    private void OnPointerPressedForResize(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edge = EdgeAt(e.GetPosition(this));
        if (edge is null) return;
        BeginResizeDrag(edge.Value, e);
        e.Handled = true;
    }

    /// A press on the bar's own background moves the window; a double click
    /// maximizes, handled here where the click count is known (AlbumWall,
    /// 2026-09-20: a separate DoubleTapped handler cannot stop the move drag
    /// already begun by the second press).
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ToggleMaximized(); e.Handled = true; return; }
        if (e.ClickCount > 2) return;
        BeginMoveDrag(e);
    }
}
