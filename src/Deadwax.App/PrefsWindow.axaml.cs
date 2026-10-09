using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Deadwax.Drive;
using Deadwax.Verify;

namespace Deadwax.App;

/// Preferences: see PrefsWindow.axaml. Reads and writes Settings itself; the
/// drive speed and eject tick bind to the main view model, whose handlers
/// already save them, so one setting has one owner.
public partial class PrefsWindow : Window
{
    private readonly MainWindow _main;
    private readonly Settings _settings;

    public PrefsWindow(MainWindow main, MainViewModel vm, Settings settings)
    {
        _main = main;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;

        PostRipBox.IsChecked = _settings.PostRip ?? true;
        ShowFiling();
        ShowLibrary();
        ShowDrive();
        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        AutoCheckBox.IsChecked = _settings.CheckForUpdates;
        FillUpdate();

        // One height for every tab: the longest one's.
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SizeToLongestTab, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// His rule, as in the family's other apps (2026-10-08): the window is as
    /// tall as its LONGEST tab and the same height on all of them, so nothing
    /// moves from tab to tab. Each tab is laid out in turn, in one go before
    /// anything is drawn, and the tallest wins; the height in the markup is the
    /// least it will be, and the screen's working height the most.
    private void SizeToLongestTab()
    {
        var shown = Tabs.SelectedIndex;
        var tallest = Height;
        for (var i = 0; i < Tabs.ItemCount; i++)
        {
            Tabs.SelectedIndex = i;
            UpdateLayout();
            if (Tabs.SelectedItem is not TabItem { Content: Control content }
                || Avalonia.VisualTree.VisualExtensions.GetVisualParent(content) is not Control host
                || host.Bounds.Height <= 0) continue;
            host.Measure(new Avalonia.Size(host.Bounds.Width, double.PositiveInfinity));
            tallest = Math.Max(tallest, Math.Ceiling(host.DesiredSize.Height + ClientSize.Height - host.Bounds.Height));
            host.InvalidateMeasure();
        }
        Tabs.SelectedIndex = shown;
        if (Screens.ScreenFromWindow(this) is { } screen)
            tallest = Math.Min(tallest, screen.WorkingArea.Height / screen.Scaling - 48);
        if (tallest > Height + 0.5) Height = tallest;
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
        _settings.Save();
        ShowFilingExample(_settings.ToFiling());
        _main.FilingChanged();
    }

    // ---- drive --------------------------------------------------------------

    private void ShowDrive()
    {
        DeviceText.Text = CdDrive.DefaultDevice;
        var drive = _main.CurrentDrive;
        if (drive is null)
        {
            DriveNameText.Text = "Put a disc in to see the drive.";
            OffsetValueText.Text = "—";
            return;
        }
        DriveNameText.Text = DriveOffsets.Key(drive.Vendor, drive.Model, drive.Revision);
        var fromWhipper = WhipperConfig.ReadOffset(drive.Vendor, drive.Model, drive.Revision);
        OffsetValueText.Text = _main.CurrentOffset is { } o
            ? $"{o:+0;-0;0} samples" + (fromWhipper is not null ? "  (from whipper's settings)" : "")
            : "not known yet";
    }

    private async void OnMeasure(object? sender, RoutedEventArgs e)
    {
        await _main.MeasureOffsetAsync();
        ShowDrive();
    }

    // ---- about --------------------------------------------------------------

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
            ?? (!UpdateService.CanUpdate ? "This is a development build, so updates are not offered."
              : _main.LastUpdateFailed ? $"The last update could not be put in place, so this is still {have}. Try it again."
              : info is null ? $"This is version {have}."
              : info.Error is { } error ? error
              : info.NothingPublished ? $"No release has been published yet. This is version {have}."
              : !info.UpdateAvailable ? $"This is the latest version, {have}."
              : info.AssetUrl is null ? $"{info.LatestTag} is out, but it has no build for this kind of computer."
              : $"Version {info.LatestTag.TrimStart('v', 'V')} is available. This is {have}. Downloads, checks the SHA-256, then restarts.");
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
            _main.LatestUpdate = await UpdateService.CheckAsync();
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

    private void OnOpenRepo(object? sender, RoutedEventArgs e) => Open(UpdateService.ProjectUrl);
    private void OnOpenAlbumWall(object? sender, RoutedEventArgs e) => Open("https://github.com/gsa700/albumwall");

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
