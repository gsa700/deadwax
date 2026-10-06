using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
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

        LibraryBox.Text = _settings.Library ?? "";
        LibraryBox.PlaceholderText = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        PostRipBox.IsChecked = _settings.PostRip;
        ShowLibraryNote();
        ShowDrive();
        VersionText.Text = $"Deadwax {UpdateService.CurrentVersion}";
        UpdateText.Text = UpdateService.CanUpdate ? "" : "A development build: updates are not offered.";
    }

    // ---- library ------------------------------------------------------------

    private void OnLibraryKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveLibrary();
    }

    private void OnLibraryEdited(object? sender, RoutedEventArgs e) => SaveLibrary();

    private void SaveLibrary()
    {
        var text = (LibraryBox.Text ?? "").Trim();
        if (text.StartsWith('~')) text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        _settings.Library = text.Length == 0 ? null : text;
        _settings.Save();
        ShowLibraryNote();
    }

    private void ShowLibraryNote()
    {
        var path = string.IsNullOrWhiteSpace(_settings.Library)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music")
            : _settings.Library;
        LibraryNote.Text = Directory.Exists(path)
            ? $"{Directory.EnumerateDirectories(path).Count()} artist folders in {path}"
            : $"{path} does not exist yet; it is created by the first rip.";
    }

    private async void OnChooseLibrary(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Library folder", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        LibraryBox.Text = path;
        SaveLibrary();
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
            OffsetValueText.Text = "";
            return;
        }
        DriveNameText.Text = DriveOffsets.Key(drive.Vendor, drive.Model, drive.Revision);
        var fromWhipper = WhipperConfig.ReadOffset(drive.Vendor, drive.Model, drive.Revision);
        var fromDeadwax = DriveOffsets.ReadOffset(drive.Vendor, drive.Model, drive.Revision);
        OffsetValueText.Text = _main.CurrentOffset is { } o
            ? $"{o:+0;-0;0} samples" + (fromWhipper is not null ? "  (whipper.conf)" : fromDeadwax is not null ? "  (drives.json)" : "")
            : "unknown";
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
            UpdateText.Text = $"{info.LatestTag} is available; the main window's title bar has the button.";
        }
        else UpdateText.Text = $"{UpdateService.CurrentVersion} is the latest.";
    }

    private void OnOpenRepo(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.ProjectUrl) { UseShellExecute = true }); }
        catch { }
    }
}
