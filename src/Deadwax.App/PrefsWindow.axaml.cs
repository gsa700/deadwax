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

        PostRipBox.IsChecked = _settings.PostRip ?? Deadwax.Core.PostRip.ToolsAvailable;
        ToolsPanel.IsVisible = Deadwax.Core.PostRip.ToolsAvailable;
        ShowLibrary();
        ShowDrive();
        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        AutoCheckBox.IsChecked = _settings.CheckForUpdates;
        UpdateText.Text = UpdateService.CanUpdate ? "" : "This is a development build, so updates are not offered.";
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

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        if (!UpdateService.CanUpdate) return;
        UpdateText.Text = "Checking...";
        var info = await _main.CheckForUpdateNowAsync();
        if (info.Error is not null) UpdateText.Text = info.Error;
        else if (info.NothingPublished) UpdateText.Text = "No release is published yet.";
        else if (info.UpdateAvailable && info.AssetUrl is not null)
        {
            _main.OfferUpdate(info);
            UpdateText.Text = $"{info.LatestTag.TrimStart('v')} is out. The button to install it is in the main window's title bar.";
        }
        else UpdateText.Text = $"You have the latest, {UpdateService.CurrentVersion}.";
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
