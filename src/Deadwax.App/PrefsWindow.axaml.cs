using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Deadwax.Drive;
using Deadwax.Verify;

namespace Deadwax.App;

/// Preferences: see PrefsWindow.axaml. Reads and writes Settings itself; the
/// eject tick and the reading choice bind to the main view model, whose
/// handlers already save them, so one setting has one owner.
public partial class PrefsWindow : Window
{
    private readonly MainWindow _main;
    private readonly Settings _settings;

    public PrefsWindow(MainWindow main, MainViewModel vm, Settings settings)
    {
        _main = main;
        _settings = settings;
        InitializeComponent();
        // The size it was dragged to last time, if it was; still no taller than
        // the main window or the screen as it opens (FitToMainWindow).
        if (_settings.PrefsWidth is { } w) Width = Math.Max(w, MinWidth);
        if (_settings.PrefsHeight is { } h) Height = Math.Max(h, MinHeight);
        DataContext = vm;

        PostRipBox.IsChecked = _settings.PostRip ?? true;
        ShowFiling();
        ShowLibrary();
        ShowDrive();
        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        AutoCheckBox.IsChecked = _settings.CheckForUpdates;
        EdgeBox.IsChecked = _settings.EdgeChannel;
        FillUpdate();
        FillInstall();

        // A page is called what its line says, for a screen reader too.
        var lines = Pages.Items.OfType<ListBoxItem>().ToList();
        for (var i = 0; i < lines.Count && i < PageList.Length; i++)
            if (lines[i].Content is string name)
            {
                Avalonia.Automation.AutomationProperties.SetName(lines[i], name);
                Avalonia.Automation.AutomationProperties.SetName(PageList[i], name);
            }
        Pages.SelectionChanged += (_, _) => ShowPage();
        Pages.SelectedIndex = 0;
        ShowPage();     // the list starts on its first line, so no change is raised for it

        // The height, IN Opened, not posted after it: a fixed-size window
        // already on screen ignored a new height (0.2.8, the trace said
        // "620 -> 749" and the window stayed 620).
        // Then where it was last time, kept on a screen (WindowPlacement); a
        // first run opens where it always has.
        Opened += (_, _) =>
        {
            FitToMainWindow();
            WindowPlacement.Restore(this, _settings.PrefsX, _settings.PrefsY);
        };
        Closing += (_, _) =>
        {
            (_settings.PrefsWidth, _settings.PrefsHeight) = (Math.Round(Width), Math.Round(Height));
            if (WindowPlacement.Current(this) is { } p) (_settings.PrefsX, _settings.PrefsY) = (p.X, p.Y);
            _settings.Save();
        };
    }

    private ScrollViewer[] PageList => [PageLibrary, PageFiling, PageDrive, PageUpdates, PageAbout];

    /// Preferences at a page, by the name on its line ("About").
    internal void Select(string page)
    {
        var lines = Pages.Items.OfType<ListBoxItem>().ToList();
        var i = lines.FindIndex(l => l.Content is string name && name == page);
        if (i >= 0) Pages.SelectedIndex = i;
    }

    /// One page at a time, each from its top.
    private void ShowPage()
    {
        var shown = Pages.SelectedIndex;
        for (var i = 0; i < PageList.Length; i++)
        {
            PageList[i].IsVisible = i == shown;
            if (i == shown) PageList[i].Offset = default;
        }
    }

    /// ONE SIZE ON EVERY PAGE (the family's rule, 2026-10-08), the markup's
    /// 640 or the size he dragged it to, and a longer page scrolls inside it. With tabs it was the longest
    /// tab's height, 964 by 2026-10-25 for the Drive tab.
    /// NO TALLER THAN THE MAIN WINDOW, his rule (2026-10-24): it is already
    /// fitted to the screen, so this one cannot run off it, and the two
    /// stand the same height. Its frame (a title bar the main window draws
    /// inside itself) comes off: as the window system gives it, else 32,
    /// GNOME's title bar, since native Wayland gives no frame size (on
    /// Techbench the two windows' bottoms then differed by that bar).
    /// The screen's working height stays a limit too, for a main window
    /// larger than the screen. Native Wayland gives a new window no screen of
    /// its own and reports the working area as the whole screen, without
    /// GNOME's bar: on Techbench's 1080 screen the screen limit alone let it
    /// open 1013 high, past the bottom.
    private void FitToMainWindow()
    {
        var height = Height;
        if (_main.Bounds.Height > 0)
        {
            var frame = FrameSize is { } f && f.Height > ClientSize.Height ? f.Height - ClientSize.Height
                : OperatingSystem.IsLinux() ? 32 : 0;
            height = Math.Min(height, _main.Bounds.Height - frame);
        }
        var screen = Screens.ScreenFromWindow(this) ?? Screens.ScreenFromWindow(_main) ?? Screens.Primary;
        if (screen is not null)
            height = Math.Min(height, screen.WorkingArea.Height / screen.Scaling - 48);
        Console.WriteLine($"[prefs] height {Height:0} -> {height:0} (main window {_main.Bounds.Height:0} high, screen {(screen is null ? "unknown" : $"{screen.WorkingArea.Height / screen.Scaling:0}")})");
        if (Math.Abs(height - Height) > 0.5) { Height = height; UpdateLayout(); }
    }

    // ---- library ------------------------------------------------------------

    private static string DefaultLibrary => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
    private string LibraryPathNow => string.IsNullOrWhiteSpace(_settings.Library) ? DefaultLibrary : _settings.Library;

    private void ShowLibrary()
    {
        var path = LibraryPathNow;
        LibraryPath.Text = path;
        LibraryNote.Text = Directory.Exists(path)
            ? $"{Directory.EnumerateDirectories(path).Count()} artist folders there now."
            : "That folder does not exist yet; the first rip creates it.";
    }

    private async void OnChooseLibrary(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Library folder", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        _settings.Library = path == DefaultLibrary ? null : path;
        _settings.Save();
        ShowLibrary();
    }

    private void OnUseDefaultLibrary(object? sender, RoutedEventArgs e)
    {
        _settings.Library = null;
        _settings.Save();
        ShowLibrary();
    }

    private void OnPostRipToggled(object? sender, RoutedEventArgs e)
    {
        _settings.PostRip = PostRipBox.IsChecked == true;
        _settings.Save();
    }

    // ---- filing -------------------------------------------------------------

    private static readonly (Deadwax.Output.FolderStyle Style, string Label)[] FolderStyles =
    [
        (Deadwax.Output.FolderStyle.ArtistYearAlbum, "Artist / 1976 - Album"),
        (Deadwax.Output.FolderStyle.ArtistAlbumYear, "Artist / Album (1976)"),
        (Deadwax.Output.FolderStyle.ArtistAlbum, "Artist / Album"),
        (Deadwax.Output.FolderStyle.ArtistDashAlbum, "Artist - Album  (one level)"),
    ];

    private static readonly (Deadwax.Output.TrackStyle Style, string Label)[] TrackStyles =
    [
        (Deadwax.Output.TrackStyle.ArtistNumberTitle, "Artist - 01 - Title.flac"),
        (Deadwax.Output.TrackStyle.NumberDashTitle, "01 - Title.flac"),
        (Deadwax.Output.TrackStyle.NumberTitle, "01 Title.flac"),
    ];

    private bool _fillingFiling;

    private void ShowFiling()
    {
        _fillingFiling = true;
        var filing = _settings.ToFiling();
        FolderStyleBox.ItemsSource = FolderStyles.Select(f => f.Label).ToList();
        FolderStyleBox.SelectedIndex = Math.Max(0, Array.FindIndex(FolderStyles, f => f.Style == filing.Folder));
        TrackStyleBox.ItemsSource = TrackStyles.Select(t => t.Label).ToList();
        TrackStyleBox.SelectedIndex = Math.Max(0, Array.FindIndex(TrackStyles, t => t.Style == filing.Track));
        EmbedBox.IsChecked = filing.EmbedCover;
        BackBox.IsChecked = filing.BackCover;
        CueBox.IsChecked = filing.Cue;
        M3uBox.IsChecked = filing.M3u;
        GainBox.IsChecked = filing.ReplayGain;
        ShowFilingExample(filing);
        _fillingFiling = false;
    }

    private void ShowFilingExample(Deadwax.Output.Filing filing)
    {
        var dir = Deadwax.Output.FileNames.AlbumPath(filing.Folder, "", "Thin Lizzy", "1976", "Jailbreak");
        FilingExample.Text = Path.Combine(dir, Deadwax.Output.FileNames.Track(filing.Track, "Thin Lizzy", 1, "Jailbreak"));
    }

    private void OnFilingChanged(object? sender, RoutedEventArgs e)
    {
        if (_fillingFiling || FolderStyleBox.SelectedIndex < 0 || TrackStyleBox.SelectedIndex < 0) return;
        _settings.FolderStyle = FolderStyles[FolderStyleBox.SelectedIndex].Style.ToString();
        _settings.TrackStyle = TrackStyles[TrackStyleBox.SelectedIndex].Style.ToString();
        _settings.EmbedCover = EmbedBox.IsChecked == true;
        _settings.BackCover = BackBox.IsChecked == true;
        _settings.WriteCue = CueBox.IsChecked == true;
        _settings.WriteM3u = M3uBox.IsChecked == true;
        _settings.ReplayGain = GainBox.IsChecked == true;
        _settings.Save();
        ShowFilingExample(_settings.ToFiling());
        _main.FilingChanged();
    }

    // ---- drive --------------------------------------------------------------

    private void ShowDrive()
    {
        // Only a device the system reports: the fallback guess named a disk
        // that was not the drive (/dev/disk2 on the Mac, 2026-10-20).
        var device = CdDrive.FoundDevice;
        DeviceText.Text = device ?? "";
        // The scanned disc's drive, or with no disc in, the kernel's record of it.
        var drive = _main.CurrentDrive ?? (device is null ? null : DriveIdentity.FromSysfs(device));
        var saved = DriveOffsets.All();
        var key = drive is null ? null : DriveOffsets.Key(drive.Vendor, drive.Model, drive.Revision);
        // Linux knows a drive without a disc (sysfs), so nothing there means
        // no drive. A Mac or Windows names one only while a disc is in it.
        DriveNameText.Text = key
            ?? (OperatingSystem.IsLinux() ? "No drive found."
                : "No disc in the drive. The drive is named, with its read offset, once a disc is in it.");
        OffsetValueText.Text = key is null ? "—"
            : saved.TryGetValue(key, out var o) ? $"{o:+0;-0;0} samples"
            : "Not measured yet. Deadwax measures it once for this drive: put in a well-known commercial CD and Measure.";
        var others = saved.Where(kv => kv.Key != key).Select(kv => $"{kv.Key}   {kv.Value:+0;-0;0}").ToList();
        SavedDrivesText.IsVisible = others.Count > 0;
        SavedDrivesText.Text = (key is null ? "Remembered:\n" : "Also remembered:\n") + string.Join("\n", others);
    }

    private async void OnMeasure(object? sender, RoutedEventArgs e)
    {
        await _main.MeasureOffsetAsync();
        ShowDrive();
    }

    // ---- updates --------------------------------------------------------------

    private bool _updating;
    private string? _updateNote;

    /// Where things stand, from whatever is known: the launch-time check may
    /// have answered already, and the last update may have failed to apply.
    internal void FillUpdate()
    {
        if (_updating) return;
        var have = UpdateService.CurrentVersion;
        var info = _main.LatestUpdate;
        UpdateButton.IsVisible = UpdateService.CanUpdate;
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = info is { UpdateAvailable: true, AssetUrl: not null }
            ? $"Update to {info.LatestTag.TrimStart('v', 'V')} and restart"
            : "Check for updates";
        UpdateText.Text = _updateNote
            ?? (!UpdateService.CanUpdate ? (UpdateService.MacBlocker is { } why
                    ? $"This is version {have}. {why}"
                    : "This is a development build, so updates are not offered.")
              : _main.LastUpdateFailed ? $"The last update could not be put in place, or did not start, so this is still {have}."
              : info is null ? $"This is version {have}."
              : info.Error is { } error ? error
              : info.NothingPublished ? $"No release has been published yet. This is version {have}."
              : !info.UpdateAvailable ? $"This is the latest version, {have}."
              : info.AssetUrl is null ? $"{info.LatestTag} is out, but it has no build for this kind of computer."
              : $"Version {info.LatestTag.TrimStart('v', 'V')}{(info.IsPrerelease ? " (Edge)" : "")} is available. This is {have}. Downloads, checks the SHA-256, then restarts.");
    }

    /// One button, two jobs, as AlbumWall's: with nothing newer known it checks;
    /// with something newer known it fetches it, checks it, and restarts into it.
    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        if (_updating || !UpdateService.CanUpdate) return;
        _updateNote = null;

        if (_main.LatestUpdate is not { UpdateAvailable: true, AssetUrl: not null })
        {
            UpdateButton.IsEnabled = false;
            UpdateText.Text = "Looking\u2026";
            _main.LatestUpdate = await UpdateService.CheckAsync(_settings.EdgeChannel);
            _main.ShowUpdateDot(_main.LatestUpdate.UpdateAvailable);   // also refills this tab
            FillUpdate();
            return;
        }

        var info = _main.LatestUpdate;
        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateBar.IsVisible = true;
        try
        {
            var progress = new Progress<double>(f =>
            {
                UpdateFill.Width = f * UpdateBar.Bounds.Width;
                UpdateText.Text = $"Downloading {info.LatestTag.TrimStart('v', 'V')}\u2026 {f:P0}";
            });
            await _main.ApplyUpdateAsync(info, progress);
            UpdateText.Text = "Checked. Restarting into the new version\u2026";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] failed: {ex.Message}");
            _updating = false;
            UpdateBar.IsVisible = false;
            UpdateFill.Width = 0;
            _updateNote = ex.Message;
            FillUpdate();
        }
    }

    private void OnAutoCheckToggled(object? sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = AutoCheckBox.IsChecked == true;
        _settings.Save();
    }

    private void OnEdgeToggled(object? sender, RoutedEventArgs e)
    {
        _settings.EdgeChannel = EdgeBox.IsChecked == true;
        _settings.Save();
        _main.LatestUpdate = null;   // what was found belongs to the other channel
        _main.ShowUpdateDot(_main.LastUpdateFailed);
        _updateNote = null;
        FillUpdate();
    }

    private void OnOpenRepo(object? sender, RoutedEventArgs e) => Open(UpdateService.ProjectUrl);
    private void OnOpenAlbumWall(object? sender, RoutedEventArgs e) => Open("https://github.com/gsa700/albumwall");

    private void Open(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        }
        catch { }
    }

    // ---- install ------------------------------------------------------------

    /// Says how this copy is running, in words, and offers the one action that
    /// fits (AlbumWall's FillInstall). Four cases, because there are four:
    /// installed; a release sitting loose wherever it was unzipped; one pinned
    /// as portable; and a development build, which is many files and cannot be
    /// installed by copying one. macOS has Deadwax.app and none of this.
    private void FillInstall()
    {
        if (!Install.InstallService.CanInstall) { InstallSection.IsVisible = false; return; }
        var dir = Install.InstallService.ExeDirectory;
        switch (Install.InstallService.Mode)
        {
            case Install.InstallMode.Installed:
                InstallStatus.Text = $"Installed in {dir}.";
                InstallButton.Content = "Uninstall…";
                InstallButton.IsVisible = true;
                break;

            case Install.InstallMode.Portable:
                InstallStatus.Text = $"Portable: runs from {dir} and registers nothing with this computer "
                                   + $"({Install.InstallLayout.PortableMarker} is beside the program).";
                InstallButton.IsVisible = false;
                break;

            default:
                if (Install.InstallService.IsSingleFile)
                {
                    InstallStatus.Text = $"Running from {dir}, not installed. Installing copies it to "
                                       + $"{Install.InstallService.InstallDirectory} and adds it to your menus.";
                    InstallButton.Content = "Install…";
                    InstallButton.IsVisible = true;
                }
                else
                {
                    InstallStatus.Text = "A development build, running from its build folder. "
                                       + "Only a published release can install itself.";
                    InstallButton.IsVisible = false;
                }
                break;
        }
    }

    /// Either answer closes the main window, so never in the middle of reading
    /// a disc: a read cut off by a closing window is what the BDR-209D answers
    /// with a USB reset.
    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { IsScanning: true } or MainViewModel { IsRipping: true })
        {
            InstallStatus.Text = "Deadwax is reading a disc. Let it finish, then try again.";
            return;
        }
        if (Install.InstallService.Mode == Install.InstallMode.Installed)
            await InstallFlow.RunUninstallAsync(_main, asked: true);
        else
            await InstallFlow.OfferInstallAsync(_main);
    }
}
