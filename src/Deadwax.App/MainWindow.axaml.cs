using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Deadwax.Core;
using Deadwax.Drive;
using Deadwax.Metadata;

namespace Deadwax.App;

public sealed partial class MainWindow : Window
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// His library, where rips go since G5 passed (2026-09-24), and whose artist
    /// spellings every rip follows.
    private static readonly string Library = Path.Combine(Home, "Music");

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
        Width = Math.Max(_settings.Width, MinWidth);
        Height = Math.Max(_settings.Height, MinHeight);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
        DataContext = _vm;
        _vm.YearChanged += UpdateOutputPreview;
        _vm.Log.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
        BuildWindowButtons();
        AddHandler(PointerPressedEvent, OnPointerPressedForResize, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedForResize, RoutingStrategies.Tunnel);
        TopBar.PointerPressed += OnTitleBarPressed;

        _vm.DriveText = CdDrive.DefaultDevice;
        _vm.StatusText = "Put a CD in the drive.";
        _watch.Tick += async (_, _) => await WatchAsync();
        Opened += async (_, _) =>
        {
            WaylandShell.ClaimIdentity(this);   // the dash's icon, on native Wayland
            _watch.Start();
            await WatchAsync();
        };
        Closing += (_, _) =>
        {
            _work?.Cancel();
            // The size of the normal window, not the maximized one, so leaving
            // maximized restores to what he last chose.
            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.Width = Bounds.Width;
                _settings.Height = Bounds.Height;
            }
            _settings.Save();
        };
    }

    // ------------------------------------------------------------ the disc

    /// Every three seconds while waiting: is there a disc? And while a disc is
    /// on screen, has it gone? udev knows without spinning the drive up.
    private async Task WatchAsync()
    {
        if (_vm.IsScanning || _vm.IsRipping) return;
        var (present, audio) = await MediaAsync(CdDrive.DefaultDevice);
        if (_vm.IsWaiting && present && audio > 0 && !_vm.HasError) await ScanAsync();
        else if (_vm.IsWaiting && present && audio == 0) _vm.StatusText = "That disc has no audio tracks.";
        else if (!present && _vm.IsDisc) Reset("Put a CD in the drive.");
        else if (!present && _vm.IsWaiting)
        {
            _vm.ErrorText = "";
            _vm.StatusText = "Put a CD in the drive.";
        }
    }

    private static async Task<(bool Present, int Audio)> MediaAsync(string device)
    {
        try
        {
            var start = new ProcessStartInfo("udevadm") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "info", "-q", "property", "-n", device }) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var present = text.Contains("ID_CDROM_MEDIA=1", StringComparison.Ordinal);
            var audioLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID_CDROM_MEDIA_TRACK_COUNT_AUDIO=", StringComparison.Ordinal));
            var audio = audioLine is null ? 0 : int.Parse(audioLine.Split('=')[1]);
            return (present, audio);
        }
        catch
        {
            return (false, 0);
        }
    }

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
            var disc = await Task.Run(() => RipSession.PrepareAsync(null, null, _ => { }, readSubchannel: false, ct: ct), ct);
            _disc = disc;
            _libraryMatch = await Task.Run(() => LibraryIndex.FindDisc(Library, disc.DiscId), ct);
            ShowDisc(disc);
            _vm.Stage = Stage.Disc;
            if (_libraryMatch is not null)
                _vm.ErrorText = $"Already in your library: {Path.GetRelativePath(Library, _libraryMatch)}";

            var first = _vm.Releases.FirstOrDefault();
            if (first is not null && _vm.Releases.Count == 1) _ = ChooseAsync(first);
            else if (first is null) _vm.ErrorText = "No MusicBrainz release has this disc ID. Paste the release link below.";

            if (_libraryMatch is null) await ReadSubchannelAsync(disc, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Reset("The disc could not be read.");
            _vm.ErrorText = e.Message;
        }
    }

    private async Task ReadSubchannelAsync(PreparedDisc disc, CancellationToken ct)
    {
        _vm.SubchannelText = "Reading ISRCs, CD-Text and gaps in the background, about two minutes. Choose meanwhile.";
        try
        {
            var full = await Task.Run(() => RipSession.ReadSubchannelAsync(disc, ct), ct);
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
                using var doc = await mb.GetReleaseAsync(id) ?? throw new MusicBrainzException("MusicBrainz has no release with that ID.");
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
            _vm.NoteText = plan.Disambiguation ?? "";
            _vm.KeepNote = false;
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

    private void UpdateOutputPreview()
    {
        if (_plan is null) return;
        _plan.KeepDisambiguation = _vm.KeepNote;
        var dir = _plan.AlbumDirectory(Library, ChosenYear);
        _vm.OutputFolder = dir.Replace(Home, "~") + "/";
        var first = _plan.Tracks.FirstOrDefault();
        _vm.OutputFiles = first is null ? "" :
            $"  {first.FileName}\n  … {_plan.Tracks.Count - 1} more tracks\n  .cue  .log  .m3u  .toc  cover.jpg" +
            (_vm.IsSetDisc && _vm.Unbox ? "\n  then filed as its own album by music-unbox" : "  back.jpg");
        var exists = Directory.Exists(dir);
        if (_libraryMatch is not null)
            _vm.ErrorText = $"Already in your library: {Path.GetRelativePath(Library, _libraryMatch)}";
        else
            _vm.ErrorText = exists ? "That folder is already in the library. Deadwax never writes over an album." : "";
        _vm.CanRip = !exists && _libraryMatch is null && _disc?.Cdrdao is not null;
    }

    private async void OnRescan(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async void OnEject(object? sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        Reset("Put a CD in the drive.");
        await EjectAsync();
    }

    private static async Task EjectAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("eject", CdDrive.DefaultDevice) { RedirectStandardError = true })!;
            await p.WaitForExitAsync();
        }
        catch { }
    }

    private void ClearDisc()
    {
        _plan?.Dispose();
        _plan = null;
        _disc = null;
        _planVersion++;
        _vm.Cover = null;
        _vm.Releases.Clear();
        _vm.ReleasesChanged();
        _vm.CanRip = false;
        _vm.HasYearChoice = false;
        _vm.IsSetDisc = false;
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
    }

    // ------------------------------------------------------------ the rip

    private async void OnRip(object? sender, RoutedEventArgs e)
    {
        if (_disc is null || _plan is null) return;
        var disc = _disc;
        var plan = _plan;
        var year = ChosenYear;
        var unbox = _vm.IsSetDisc && _vm.Unbox;
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
        _vm.Stage = Stage.Ripping;
        _watch.Stop();

        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        var observer = new Observer(_vm, disc);
        try
        {
            var result = await Task.Run(() => RipSession.RipAsync(disc, plan, year, Library, observer, cover: cover, ct: ct), ct);
            observer.Finish();
            var steps = new List<StepRow>();
            var ok = result.Tracks.Count(t => t.CopyOk);
            steps.Add(new StepRow
            {
                Name = "Read", Ok = result.AllOk,
                Detail = result.AllOk ? $"{ok} tracks, every one read twice alike" : $"{result.Tracks.Count - ok} track(s) never read the same twice",
            });
            var accurate = result.Tracks.Where(t => t.AccurateRip.IsAccurate).ToList();
            steps.Add(new StepRow
            {
                Name = "AccurateRip", Ok = accurate.Count == result.Tracks.Count,
                Detail = accurate.Count == 0 ? "No match (the disc may not be in the database)"
                    : $"{accurate.Count} of {result.Tracks.Count} accurate, {accurate.Min(t => t.AccurateRip.Confidence)}–{accurate.Max(t => t.AccurateRip.Confidence)} matching rips",
            });

            var dir = result.AlbumDirectory;
            if (result.AllOk)
            {
                _vm.NowText = "Into the library: music-unbox, music-backart, music-audit...";
                var after = await Task.Run(() => PostRip.RunAsync(dir, unbox, observer.Say, ct), ct);
                if (after.AlbumDirectory != dir)
                    steps.Add(new StepRow { Name = "Filed as its own album", Ok = true, Detail = Path.GetFileName(after.AlbumDirectory) });
                dir = after.AlbumDirectory;
                steps.Add(new StepRow { Name = "Front cover", Ok = File.Exists(Path.Combine(dir, "cover.jpg")), Detail = File.Exists(Path.Combine(dir, "cover.jpg")) ? "cover.jpg" : "None in the Cover Art Archive" });
                steps.Add(new StepRow { Name = "Back cover", Ok = File.Exists(Path.Combine(dir, "back.jpg")), Detail = File.Exists(Path.Combine(dir, "back.jpg")) ? "back.jpg" : "None in the Cover Art Archive" });
                steps.Add(new StepRow { Name = "Library audit", Ok = after.AuditClean, Detail = after.AuditClean ? "music-audit: no issues" : "music-audit found issues; see the engine log" });
                foreach (var note in after.Notes) _vm.Log.Add("note: " + note);
            }

            _vm.AllGood = steps.All(s => s.Ok);
            _vm.Verdict = result.AllOk
                ? accurate.Count == result.Tracks.Count ? $"All {result.Tracks.Count} tracks accurately ripped" : $"All {result.Tracks.Count} tracks ripped"
                : "Ripped, with problems";
            _vm.VerdictDetail = result.AllOk
                ? "Every track read twice with the same result."
                : "Some tracks never read the same way twice. Clean the disc and rip it again.";
            _vm.Steps.Clear();
            foreach (var s in steps) _vm.Steps.Add(s);
            _vm.AlbumDirectory = dir.Replace(Home, "~") + "/";
            _vm.Files.Clear();
            var flacs = Directory.EnumerateFiles(dir, "*.flac").Count();
            _vm.Files.Add($"  {flacs} × FLAC, tagged, with MusicBrainz IDs");
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".flac")).Select(Path.GetFileName).Order())
                _vm.Files.Add("  " + f);
            _lastAlbum = dir;
            _vm.Stage = Stage.Intake;
        }
        catch (OperationCanceledException)
        {
            Reset("Stopped.");
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

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false })?.Dispose(); }
        catch { }
    }

    private async void OnNextDisc(object? sender, RoutedEventArgs e)
    {
        Reset("Put the next CD in the drive.");
        await EjectAsync();
    }

    /// The rip's reports, carried to the window. Progress arrives per sector, so
    /// it is thinned to whole percent before it crosses to the UI thread.
    private sealed class Observer(MainViewModel vm, PreparedDisc disc) : IRipObserver
    {
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
        });

        public void TrackStarted(int track) => Dispatcher.UIThread.Post(() =>
        {
            var row = vm.Tracks.FirstOrDefault(t => t.Number == track);
            if (row is null) return;
            row.Active = true;
            row.Background = Tone.ActiveRow;
            row.Foreground = Tone.Text;
            row.Status = "";
            row.Detail = "test";
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
                if (row is not null) { row.Progress = pct; row.Detail = pass; if (speed > 0) row.Speed = $"{speed:0.0}×"; }
                vm.Overall = 100.0 * workNow / _totalWork;
                vm.NowText = $"Track {track} of {vm.Tracks.Count} · {pass} pass";
                if (speed > 0) vm.SpeedText = $"{speed:0.0}×";
                vm.ElapsedText = _clock.Elapsed.ToString(@"m\:ss");
                if (pass == "test")
                {
                    var last = (t.StartLsn + done) / SectorsPerCell;
                    for (var c = t.StartLsn / SectorsPerCell; c <= last && c < vm.Map.Count; c++)
                        if (!_worst.ContainsKey(c) && vm.Map[c].Fill == Tone.Unread) vm.Map[c].Fill = Tone.Reading;
                }
            });
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
                if (!_worst.TryGetValue(cell, out var had) || had == Tone.Warn) _worst[cell] = brush;
            }
        }

        public void TrackFinished(RippedTrack result) => Dispatcher.UIThread.Post(() =>
        {
            var t = disc.Toc.Track(result.Number);
            for (var c = t.StartLsn / SectorsPerCell; c <= (disc.Toc.EndLsn(t) - 1) / SectorsPerCell && c < vm.Map.Count; c++)
                vm.Map[c].Fill = _worst.TryGetValue(c, out var b) ? b : result.CopyOk ? Tone.MapGood : Tone.Bad;

            var row = vm.Tracks.FirstOrDefault(r => r.Number == result.Number);
            if (row is null) return;
            row.Active = false;
            row.Background = Avalonia.Media.Brushes.Transparent;
            row.TestCrc = result.Crc.ToString("X8");
            row.CopyCrc = result.Crc.ToString("X8");
            if (!result.CopyOk) { row.Status = "Never read alike"; row.StatusBrush = Tone.Bad; row.Detail = $"{result.Passes} passes"; return; }
            if (result.AccurateRip.IsAccurate) { row.Status = "✓ Accurate"; row.StatusBrush = Tone.Good; row.Detail = $"{result.AccurateRip.Confidence} matching rips"; }
            else { row.Status = "Copy OK"; row.StatusBrush = Tone.Good; row.Detail = "no AccurateRip match"; }
            if (result.Passes > 2) row.Detail += $" · {result.Passes} passes";
        });

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
        var (left, right) = WindowButtons.Layout();
        foreach (var k in left) LeftButtons.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        foreach (var k in right) RightButtons.Children.Add(WindowButtons.Create(k, () => Invoke(k)));

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

    private const double ResizeBand = 7;
    private StandardCursorType _cursor = StandardCursorType.Arrow;

    private WindowEdge? EdgeAt(Point p)
    {
        if (WindowState != WindowState.Normal) return null;
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
