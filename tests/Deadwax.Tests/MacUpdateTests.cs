using System.Diagnostics;
using Deadwax.App;

namespace Deadwax.Tests;

/// The Mac self-update (MacUpdate): the checks run against real bundles where
/// this is a Mac that has them, the notarized /Applications/Deadwax.app (only
/// read) and an unnotarized dist/ build; the swap runs on fake bundles.
public class MacUpdateTests
{
    private const string Installed = "/Applications/Deadwax.app";

    private static string? InstalledVersion()
    {
        if (!OperatingSystem.IsMacOS() || !Directory.Exists(Installed)) return null;
        var p = Process.Start(new ProcessStartInfo("/usr/bin/plutil")
        {
            ArgumentList = { "-extract", "CFBundleShortVersionString", "raw", "-o", "-", Installed + "/Contents/Info.plist" },
            RedirectStandardOutput = true,
        })!;
        var v = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return v;
    }

    [Fact]
    public void A_notarized_newer_Deadwax_passes()
    {
        if (InstalledVersion() is not { } v) return;
        MacUpdate.Verify(Installed, "v" + v, "0.0.1");
    }

    [Fact]
    public void The_same_or_an_older_version_is_refused()
    {
        if (InstalledVersion() is not { } v) return;
        Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(Installed, "v" + v, v));
        Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(Installed, "v" + v, "99.0.0"));
    }

    [Fact]
    public void An_app_that_is_not_the_version_the_release_names_is_refused()
    {
        if (InstalledVersion() is null) return;
        var e = Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(Installed, "v99.0.0", "0.0.1"));
        Assert.Contains("not 99.0.0", e.Message);
    }

    [Fact]
    public void A_copy_signed_by_anyone_else_is_refused()
    {
        if (InstalledVersion() is not { } v) return;
        var dir = Directory.CreateTempSubdirectory("deadwax-macupdate-").FullName;
        try
        {
            var app = Path.Combine(dir, "Deadwax.app");
            Run("/usr/bin/ditto", Installed, app);
            Run("/usr/bin/codesign", "--force", "--deep", "--sign", "-", app);   // ad hoc: no team
            var e = Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(app, "v" + v, "0.0.1"));
            Assert.Contains("not signed as Deadwax", e.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_zip_holding_anything_beside_the_app_is_refused()
    {
        if (InstalledVersion() is not { } v) return;
        var dir = Directory.CreateTempSubdirectory("deadwax-macupdate-").FullName;
        try
        {
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(src);
            Run("/usr/bin/ditto", Installed, Path.Combine(src, "Deadwax.app"));
            File.WriteAllText(Path.Combine(src, "extra"), "x");
            var zip = Path.Combine(dir, "u.zip");
            Run("/usr/bin/ditto", "-c", "-k", src, zip);
            var e = Assert.Throws<InvalidOperationException>(() => MacUpdate.UnpackAndVerify(zip, Path.Combine(dir, "ex"), "v" + v, "0.0.1"));
            Assert.Contains("Deadwax.app alone", e.Message);

            var good = Path.Combine(dir, "good.zip");
            Run("/usr/bin/ditto", "-c", "-k", "--keepParent", Installed, good);
            Assert.Equal(Path.Combine(dir, "ex2", "Deadwax.app"), MacUpdate.UnpackAndVerify(good, Path.Combine(dir, "ex2"), "v" + v, "0.0.1"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("/Volumes/Deadwax 0.7.0/Deadwax.app/Contents/MacOS/Deadwax", "disk image")]
    [InlineData("/private/var/folders/x/T/AppTranslocation/AB12/d/Deadwax.app/Contents/MacOS/Deadwax", "disk image")]
    [InlineData("/usr/local/bin/Deadwax", "not inside Deadwax.app")]
    public void A_copy_that_cannot_replace_itself_says_why(string exe, string why)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Contains(why, MacUpdate.Blocker(exe));
    }

    private static string Helper(string root) =>
        MacUpdate.ApplyScript.Replace("/usr/bin/open -n", $"echo opened >> '{root}/opened'; :");

    private static (string Root, string Target, string Staged, string Marker) FakeBundles()
    {
        var root = Directory.CreateTempSubdirectory("deadwax-macswap-").FullName;
        var target = Path.Combine(root, "Applications", "Deadwax.app");
        Directory.CreateDirectory(Path.Combine(target, "Contents"));
        File.WriteAllText(Path.Combine(target, "Contents", "version"), "old");
        var staged = Path.Combine(root, "stage", "ex", "Deadwax.app");
        Directory.CreateDirectory(Path.Combine(staged, "Contents"));
        File.WriteAllText(Path.Combine(staged, "Contents", "version"), "new");
        return (root, target, staged, Path.Combine(root, ".update-failed"));
    }

    private static void RunHelper(string root, params string[] args)
    {
        var sh = Path.Combine(root, "apply.sh");
        File.WriteAllText(sh, Helper(root) + "\n");
        Run("/bin/sh", [sh, "999999", .. args]);
    }

    [Fact]
    public void The_helper_swaps_the_whole_bundle_and_relaunches()
    {
        if (OperatingSystem.IsWindows()) return;   // the helper is a /bin/sh script
        var (root, target, staged, marker) = FakeBundles();
        try
        {
            File.WriteAllText(marker, "");
            RunHelper(root, staged, target, marker, Path.Combine(root, "stage"), Path.Combine(root, "helper"));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Contents", "version")));
            Assert.Single(Directory.GetFileSystemEntries(Path.Combine(root, "Applications")));   // no old bundle left
            Assert.False(File.Exists(marker));
            Assert.False(Directory.Exists(Path.Combine(root, "stage")));
            Assert.True(File.Exists(Path.Combine(root, "opened")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void When_the_new_bundle_cannot_go_in_the_old_one_stays_and_says_so()
    {
        if (OperatingSystem.IsWindows()) return;   // the helper is a /bin/sh script
        var (root, target, _, marker) = FakeBundles();
        try
        {
            RunHelper(root, Path.Combine(root, "nothing-here"), target, marker, Path.Combine(root, "stage"), Path.Combine(root, "helper"));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Contents", "version")));
            Assert.Single(Directory.GetFileSystemEntries(Path.Combine(root, "Applications")));
            Assert.True(File.Exists(marker));
            Assert.True(File.Exists(Path.Combine(root, "opened")));   // the old one is started again
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Values_with_shell_characters_are_only_ever_arguments()
    {
        if (OperatingSystem.IsWindows()) return;   // the helper is a /bin/sh script
        var (root, _, _, _) = FakeBundles();
        try
        {
            var odd = Path.Combine(root, "Apps $(touch PWNED) `touch PWNED2` it's");
            var target = Path.Combine(odd, "Deadwax.app");
            Directory.CreateDirectory(Path.Combine(target, "Contents"));
            File.WriteAllText(Path.Combine(target, "Contents", "version"), "old");
            var staged = Path.Combine(root, "stage", "ex", "Deadwax.app");
            RunHelper(root, staged, target, Path.Combine(root, ".update-failed"), Path.Combine(root, "stage"), Path.Combine(root, "helper"));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Contents", "version")));
            Assert.Empty(Directory.GetFiles(root, "PWNED*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
}
