using Deadwax.Drive;

namespace Deadwax.Cli;

/// Step 3's proof (spec §11), as Phase B proved the subchannel reader: the
/// disc in the drive read through libcdio and through Deadwax's own drive
/// access, side by side, and every difference named. The drive list, the
/// drive's identity, the TOC and each track's control bits. Reads the TOC
/// twice; no audio.
internal static class CheckDriveCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("deadwax: check-drive compares Deadwax's own drive access with libcdio's; so far it exists on macOS and Linux.");
            return Task.FromResult(2);
        }
        var differences = 0;
        void Line(string what, string ours, string theirs)
        {
            var same = ours == theirs;
            if (!same) differences++;
            Console.WriteLine($"  {(same ? "same" : "DIFF")}  {what,-14} {ours}{(same ? "" : $"   (libcdio: {theirs})")}");
        }

        // The drive list: macOS from DiskArbitration; Linux from /dev, as it always was.
        var ourDevices = OperatingSystem.IsMacOS() ? MacDisc.CdDevices() : CdDrive.FoundDevice is { } found ? [found] : [];
        var cdioDevices = OperatingSystem.IsMacOS() ? CdDrive.LibCdioDevices().Where(MacDisc.IsCd).ToList() : CdDrive.LibCdioDevices().ToList();
        Line("drives", string.Join(" ", ourDevices), string.Join(" ", cdioDevices));
        var device = args.Length > 1 && args[0] == "--device" ? args[1] : ourDevices.FirstOrDefault();
        if (device is null) { Console.WriteLine("No CD in any drive."); return Task.FromResult(1); }

        using var cdio = CdDrive.Open(device, libcdio: true);
        var theirToc = cdio.ReadToc();
        var ourIdentity = OperatingSystem.IsMacOS() ? MacDisc.Identity(device) : DriveIdentity.FromSysfs(device);
        Line("identity", ourIdentity?.ToString() ?? "(none)", cdio.ReadIdentity()?.ToString() ?? "(none)");

        using var own = IDriveTransport.Open(device);
        var answer = own.ReadFullToc() ?? throw new DriveException("The drive gave no full TOC.");
        var (ourToc, control) = FullToc.Parse(answer);
        Line("tracks", $"{ourToc.FirstTrack}-{ourToc.LastTrack}", $"{theirToc.FirstTrack}-{theirToc.LastTrack}");
        Line("lead-out", ourToc.LeadoutLsn.ToString(), theirToc.LeadoutLsn.ToString());
        foreach (var t in theirToc.Tracks)
        {
            var o = ourToc.Tracks.FirstOrDefault(x => x.Number == t.Number);
            var (pre, copy, four) = cdio.TrackFlags(t.Number);
            var theirs = $"{t.StartLsn} {(t.IsAudio ? "audio" : "data")}{(pre ? " pre" : "")}{(copy ? " copy" : "")}{(four ? " 4ch" : "")}";
            var c = control.GetValueOrDefault(t.Number);
            var ours = o is null ? "(missing)" :
                $"{o.StartLsn} {(o.IsAudio ? "audio" : "data")}{((c & 1) != 0 ? " pre" : "")}{((c & 2) != 0 ? " copy" : "")}{((c & 8) != 0 ? " 4ch" : "")}";
            Line($"track {t.Number}", ours, theirs);
        }
        Console.WriteLine(differences == 0 ? "\nSAME: Deadwax's own drive access reads this disc as libcdio does." : $"\n{differences} difference(s).");
        return Task.FromResult(differences == 0 ? 0 : 1);
    }
}
