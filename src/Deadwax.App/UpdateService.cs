using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Deadwax.Core;

namespace Deadwax.App;

/// What a check found. Error is a sentence that can be shown as it is.
public sealed class UpdateInfo
{
    public string CurrentVersion { get; init; } = "";
    public string LatestTag { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string ReleaseUrl { get; set; } = "";
    public string? AssetUrl { get; set; }
    public string? AssetName { get; set; }
    public string? SumsUrl { get; set; }
    /// The feed answered 404: no full release yet, or the repository is private.
    public bool NothingPublished { get; set; }
    public string? Error { get; set; }
}

/// The in-app updater, ported from AlbumWall (Install/UpdateService.cs) with the
/// parts Deadwax does not have taken out: no Windows path yet (the drive layer
/// is Linux-only, spec §8), no kiosk supervision, no install service. What is
/// kept is what matters: ask GitHub for the latest release, download the build
/// for this platform, refuse it unless the release's SHA256SUMS vouches for it,
/// then hand over to a helper that waits for exit, swaps the executable and
/// relaunches it. A program fetching a program and running it is verified or
/// not installed.
///
/// A release, as this reads it (tools/release.sh writes exactly this): a
/// normal, non-pre-release GitHub release tagged vX.Y.Z carrying
/// Deadwax-linux-x64.zip (the single-file program) and SHA256SUMS.
/// /releases/latest never returns drafts or pre-releases, and answers 404 for a
/// private repository.
///
/// DEADWAX_UPDATE_FEED points the check at another URL serving the same JSON,
/// so the whole path can be run against a local server before anything is
/// public. Logged loudly when set.
public static class UpdateService
{
    public const string Repo = "gsa700/deadwax";
    public const string ProjectUrl = "https://github.com/" + Repo;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string ExeFileName => "Deadwax";
    public static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the current executable path.");

    /// True for a published build: one executable with everything inside it.
    /// A bundled assembly has no location on disk, which is the documented
    /// way to tell. A development build is a folder of assemblies and is
    /// never offered an update.
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);

    public static bool CanUpdate => IsSingleFile && OperatingSystem.IsLinux();

    /// The version this copy reports, without the source-revision suffix.
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// The runtime identifier in the asset name: linux-x64, linux-arm64.
    public static string Rid()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    private static string FeedUrl
    {
        get
        {
            var other = Environment.GetEnvironmentVariable("DEADWAX_UPDATE_FEED");
            if (string.IsNullOrWhiteSpace(other)) return $"https://api.github.com/repos/{Repo}/releases/latest";
            Console.WriteLine($"[update] FEED OVERRIDDEN by DEADWAX_UPDATE_FEED: {other}");
            return other;
        }
    }

    /// On Linux .NET does TLS and hashing through the system's OpenSSL, loaded
    /// by name the first time either is used. If no version it knows is there
    /// the runtime prints "No usable version of libssl was found" and ABORTS
    /// THE PROCESS, uncatchable: Fedora 45 did that to AlbumWall mid-song
    /// (2026-09-21). So look first, with the names the runtime itself tries
    /// (dotnet/runtime opensslshim.c), and treat "none" as a reason not to
    /// update rather than a crash. CLR_OPENSSL_VERSION_OVERRIDE is honoured.
    private static readonly Lazy<string?> MissingTls = new(() =>
    {
        if (!OperatingSystem.IsLinux()) return null;
        var names = new List<string>();
        if (Environment.GetEnvironmentVariable("CLR_OPENSSL_VERSION_OVERRIDE") is { Length: > 0 } v)
            names.Add($"libssl.so.{v}");
        names.AddRange(["libssl.so.3", "libssl.so.1.1", "libssl.so.1.0.2", "libssl.so.1.0.0", "libssl.so.10"]);
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return null;
            }
        Console.WriteLine($"[update] no libssl .NET can use (tried {string.Join(", ", names)}); updates are off");
        return "This computer has no version of OpenSSL that .NET can use, so Deadwax cannot check for "
             + "updates. On Fedora, installing openssl3-libs fixes it.";
    });

    public static async Task<UpdateInfo> CheckAsync()
    {
        var info = new UpdateInfo { CurrentVersion = CurrentVersion, ReleaseUrl = ProjectUrl + "/releases/latest" };
        if (MissingTls.Value is { } why)
        {
            info.Error = why;
            return info;
        }
        try
        {
            using var req = Request(FeedUrl, "Deadwax-UpdateCheck");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                info.NothingPublished = true;
                Console.WriteLine("[update] nothing published (404)");
                return info;
            }
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            info.LatestTag = root.GetProperty("tag_name").GetString() ?? "";
            if (root.TryGetProperty("html_url", out var hu) && hu.GetString() is { Length: > 0 } url) info.ReleaseUrl = url;

            var wanted = $"Deadwax-{Rid()}.zip";
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var link = a.GetProperty("browser_download_url").GetString();
                    if (name == wanted) { info.AssetUrl = link; info.AssetName = name; }
                    else if (name == "SHA256SUMS") info.SumsUrl = link;
                }

            // Unparseable on either side is "not newer", never "newer" (VersionOrder).
            info.UpdateAvailable = VersionOrder.IsNewer(info.LatestTag, CurrentVersion);
            Console.WriteLine($"[update] latest {info.LatestTag}, have {CurrentVersion}, newer={info.UpdateAvailable}, "
                            + $"asset={(info.AssetUrl is null ? "none for " + Rid() : wanted)}");
        }
        catch (Exception ex)
        {
            info.Error = ex is HttpRequestException or TaskCanceledException
                ? "Could not reach GitHub. Check the network and try again."
                : ex.Message;
            Console.WriteLine($"[update] check failed: {ex.Message}");
        }
        return info;
    }

    /// Where the update is downloaded and unpacked. The relaunched program
    /// must never have this as its working directory: a directory in use as
    /// one cannot be deleted, and the next update's clean-up would throw.
    private static string StageRoot => Path.Combine(Path.GetTempPath(), "deadwax-update");

    /// Downloads the release's zip, checks it against SHA256SUMS, unpacks it
    /// and returns the staged executable. progress is the fraction downloaded.
    public static async Task<string> DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (MissingTls.Value is { } why) throw new InvalidOperationException(why);
        if (info.AssetUrl is null || info.AssetName is null)
            throw new InvalidOperationException($"This release has no build for {Rid()}.");
        if (info.SumsUrl is null)
            throw new InvalidOperationException("This release publishes no SHA256SUMS, so its download cannot be checked. It has not been installed.");

        var tmp = StageRoot;
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        // The list first: it is 100 bytes, and without the zip's line there is
        // no point fetching forty megabytes.
        string sums;
        using (var req = Request(info.SumsUrl, "Deadwax-UpdateInstall"))
        using (var resp = await Http.SendAsync(req))
        {
            resp.EnsureSuccessStatusCode();
            sums = await resp.Content.ReadAsStringAsync();
        }
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 2 && f[1].TrimStart('*', '.', '/') == info.AssetName)
            .Select(f => f[0].ToLowerInvariant())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"SHA256SUMS does not list {info.AssetName}. Not installed.");

        var zip = Path.Combine(tmp, "update.zip");
        using (var req = Request(info.AssetUrl, "Deadwax-UpdateInstall"))
        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var from = await resp.Content.ReadAsStreamAsync();
            await using var to = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (actual != expected)
        {
            Directory.Delete(tmp, recursive: true);
            throw new InvalidOperationException("The download does not match the release's SHA-256 and has been thrown away. Nothing was installed.");
        }
        Console.WriteLine($"[update] {info.AssetName} matches SHA256SUMS ({actual[..12]}...)");

        var unpacked = Path.Combine(tmp, "ex");
        ZipFile.ExtractToDirectory(zip, unpacked, overwriteFiles: true);
        File.Delete(zip);
        var staged = Directory.GetFiles(unpacked, ExeFileName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{ExeFileName} is not in the downloaded package.");

        // A last look before it is given the keys: an ELF file, and not a stub.
        var head = new byte[4];
        await using (var fs = File.OpenRead(staged)) _ = await fs.ReadAsync(head);
        if (!(head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') || new FileInfo(staged).Length < 1_000_000)
            throw new InvalidOperationException("The downloaded program is not an executable for this system.");
        return staged;
    }

    private static HttpRequestMessage Request(string url, string agent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue(agent, CurrentVersion));
        return req;
    }

    /// Launches the detached helper that waits for this process to exit,
    /// replaces the executable with the staged one and relaunches it. THE
    /// CALLER MUST THEN EXIT THE APP.
    public static void ApplyAndRestart(string stagedExe)
    {
        var target = ExePath;
        var marker = FailedMarkerPath(target);
        var pid = Environment.ProcessId;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();   // relaunch as launched

        // The helper lives in the temp root, not in the staging folder: it
        // deletes that folder, and a script cannot sit in the folder it removes.
        var sh = Path.Combine(Path.GetTempPath(), "deadwax-apply-update.sh");
        File.WriteAllText(sh, ApplyScript(pid, stagedExe, target, marker, Path.GetDirectoryName(target)!, StageRoot, OwnExtractionDir, sh, args));
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"\"{sh}\"",
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),   // not ours: see StageRoot
        });
        Console.WriteLine($"[update] helper started; swapping {target} once pid {pid} has gone; then removing {OwnExtractionDir ?? "(nothing unpacked)"}");
    }

    /// The POSIX shell helper (AlbumWall's UpdateApplyScript.Unix, without
    /// the supervised branch). Linux replaces a running program's file
    /// happily, so the retry is for a busy filesystem, not a lock.
    private static string ApplyScript(int pid, string stagedExe, string targetExe, string failedMarker,
        string workingDirectory, string stageRoot, string? ownExtractionDir, string scriptPath, IReadOnlyList<string> relaunchArgs)
    {
        // Inside single quotes the shell reads everything literally, so an
        // apostrophe is written by closing the quotes, escaping one, and opening them again.
        static string Q(string path) => "'" + path.Replace("'", "'\\''") + "'";
        var args = relaunchArgs.Count > 0 ? " " + string.Join(" ", relaunchArgs.Select(Q)) : "";
        return
            "#!/bin/sh\n" +
            $"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done\n" +
            "ok=0\n" +
            "for i in 1 2 3 4 5; do\n" +
            $"  if cp -f {Q(stagedExe)} {Q(targetExe)}; then ok=1; break; fi\n" +
            "  sleep 0.5\n" +
            "done\n" +
            "if [ \"$ok\" = 1 ]; then\n" +
            $"  chmod +x {Q(targetExe)}\n" +
            $"  rm -f {Q(failedMarker)}\n" +
            // The OLD build's unpacked native libraries; the new one unpacks into a folder of its own.
            (ownExtractionDir is null ? "" : $"  pgrep -x {ExeFileName} >/dev/null 2>&1 || rm -rf {Q(ownExtractionDir)}\n") +
            "else\n" +
            $"  : > {Q(failedMarker)}\n" +
            "fi\n" +
            $"(cd {Q(workingDirectory)} && {Q(targetExe)}{args} &)\n" +
            $"rm -rf {Q(stageRoot)}\n" +
            $"rm -f {Q(scriptPath)}\n";
    }

    private static string FailedMarkerPath(string targetExe) =>
        Path.Combine(Path.GetDirectoryName(targetExe) ?? ".", ".deadwax-update-failed");

    /// True, once, if the last helper could not swap the executable (it
    /// relaunched the old one). Clears the marker, so the warning shows on the
    /// next start only.
    public static bool ConsumeUpdateFailed()
    {
        try
        {
            var p = FailedMarkerPath(Environment.ProcessPath ?? "");
            if (File.Exists(p)) { File.Delete(p); return true; }
        }
        catch { /* a marker we cannot read is a warning we do not show */ }
        return false;
    }

    /// Where this single-file build unpacked its native libraries
    /// (IncludeNativeLibrariesForSelfExtract): ~/.net/Deadwax/<bundle id>/.
    /// The host publishes it in NATIVE_DLL_SEARCH_DIRECTORIES. Null for a
    /// development build, or when nothing was unpacked.
    private static string? OwnExtractionDir
    {
        get
        {
            try
            {
                var root = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".net");
                root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var dirs = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "";
                foreach (var d in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var full = Path.GetFullPath(d);
                    if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
                    // <root>/<app name>/<bundle id>/: the bundle's own folder.
                    var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return Path.Combine(root, parts[0], parts[1]);
                }
            }
            catch { /* then there is nothing to clean, which is safe */ }
            return null;
        }
    }
}
