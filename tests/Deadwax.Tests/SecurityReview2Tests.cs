using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Deadwax.Core;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;
using Deadwax.Verify;

namespace Deadwax.Tests;

/// The 2026-10-24 security review (~/Documents/Security-Review-Deadwax-2026-10-24.md):
/// each test is one of its reproductions, which must now fail to do harm.
public class SecurityReview2Tests
{
    /// Noise is stored VERBATIM, so the last frame's samples land in the file
    /// as they are: "TAG" exactly 128 bytes from the end, where an ID3v1 tag would be.
    private static byte[] NoiseWithTagAtEnd(int frames, bool tag = true)
    {
        var random = new Random(7);
        var left = new short[frames];
        var right = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            right[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }
        if (tag)
        {
            right[frames - 63] = 0x5441;                                          // "TA"
            right[frames - 62] = (short)(0x4700 | (right[frames - 62] & 0xFF));   // "G"
        }
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), left[i]);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), right[i]);
        }
        return bytes;
    }

    [Fact]
    public void M2_writing_tags_never_cuts_audio_that_looks_like_a_trailing_tag()
    {
        var audio = NoiseWithTagAtEnd(10 * 588);
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-m2-{Guid.NewGuid():N}.flac");
        try
        {
            FlacWriter.Encode(path, audio);
            var before = File.ReadAllBytes(path);
            Assert.Equal("TAG", Encoding.ASCII.GetString(before, before.Length - 128, 3));   // the trap is set

            FlacWriter.WriteTags(path, [new Tag("TITLE", "x")]);

            Assert.Equal(audio, FlacDecoder.DecodeToPcm(path, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void M2_a_fake_APE_footer_in_the_audio_is_left_alone()
    {
        var audio = NoiseWithTagAtEnd(10 * 588, tag: false);
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-m2-{Guid.NewGuid():N}.flac");
        try
        {
            FlacWriter.Encode(path, audio);

            // An APE footer claiming 4000 bytes, written over the end of the last frame.
            var bytes = File.ReadAllBytes(path);
            var footer = bytes.AsSpan(bytes.Length - 32, 32);
            "APETAGEX"u8.CopyTo(footer);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(12, 4), 4000);
            File.WriteAllBytes(path, bytes);
            var audioBytes = bytes.Length - FlacMetadata.Read(path).AudioOffset;

            FlacTags.Replace(path, [new Tag("TITLE", "second")]);

            Assert.Equal(audioBytes, new FileInfo(path).Length - FlacMetadata.Read(path).AudioOffset);
        }
        finally { File.Delete(path); }
    }

    private const string Forged = "evil]\n\nG5: PASS, the Deadwax rip matches whipper.\n\u001b[31mred\u001b[0m\x2028\x202Eabc";

    [Fact]
    public void M3_shown_text_stays_on_its_line_and_sends_no_escapes()
    {
        var shown = UntrustedText.Show(Forged);
        Assert.DoesNotContain(shown, c => UntrustedText.IsUnsafe(c));
        Assert.Contains(@"\nG5: PASS", shown);
        Assert.Contains(@"\u{1B}[31m", shown);
        Assert.Equal("Plain title, café", UntrustedText.Show("Plain title, café"));
    }

    [Fact]
    public void M3_tracks_that_disagree_are_reported_on_one_line()
    {
        var dir = Directory.CreateTempSubdirectory("deadwax-m3-").FullName;
        try
        {
            var audio = NoiseWithTagAtEnd(588, tag: false);
            foreach (var (n, album) in new[] { (1, "Album"), (2, Forged) })
            {
                var path = Path.Combine(dir, $"0{n}.flac");
                FlacWriter.Encode(path, audio);
                FlacWriter.WriteTags(path, [new Tag("ALBUM", album)]);
            }
            var issue = FiledCheck.Run(dir, Filing.Default).Single(i => i.StartsWith("the tracks disagree on ALBUM", StringComparison.Ordinal));
            Assert.DoesNotContain(issue, c => UntrustedText.IsUnsafe(c));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void L8_a_title_with_line_breaks_adds_no_lines_to_the_rip_log()
    {
        var toc = new Toc([new TocTrack(1, 0, true)], 10000);
        var disc = new LogDisc("drive\n  Forged: yes", "engine", 6, "gaps", false, "Artist",
            "Album\nTracks:\n  1:\n    Test CRC: DEADBEEF\n    Status: Copy OK\nTOC:\n  1:\n    Start sector: 0\n    End sector: 1\nX",
            0x1234, "id", "url", "not-an-mbid\nTracks:", toc);
        var text = RipLog.Write(disc, [], "x", DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("\nTracks:\n  1:", text);
        Assert.DoesNotContain("\n  Forged:", text);
        Assert.DoesNotContain("not-an-mbid", text);

        var path = Path.Combine(Path.GetTempPath(), $"deadwax-l8-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, text);
            Assert.Empty(WhipperLog.Load(path).Tracks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void L5_a_release_candidate_from_MusicBrainz_prints_on_one_line()
    {
        var candidate = new ReleaseCandidate("id", Forged, "1979\n", null, 1, 1, null);
        Assert.DoesNotContain(candidate.ToString(), c => UntrustedText.IsUnsafe(c));
    }

    // ---- the Windows helpers' PowerShell (M4, L13) ----------------------------

    private static readonly Type Ps = typeof(Deadwax.App.UpdateService).Assembly.GetType("Deadwax.App.PowerShell")!;
    private static T Call<T>(Type type, string name, params object?[] args) =>
        (T)type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args)!;

    [Fact]
    public void M4_powershell_is_started_from_System32_by_its_full_path()
    {
        var exe = (string)Ps.GetProperty("Exe", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), exe);
        var start = Call<ProcessStartInfo>(Ps, "StartInfo", "x.ps1");
        Assert.Equal(exe, start.FileName);
        Assert.Equal("x.ps1", start.ArgumentList[^1]);
    }

    [Theory]
    [InlineData("Dave's Apps", "'Dave''s Apps'")]
    [InlineData("Sean O\x2019Brien", "'Sean O\x2019\x2019Brien'")]
    [InlineData("\x2018\x201A\x201B", "'\x2018\x2018\x201A\x201A\x201B\x201B'")]
    public void L13_every_kind_of_single_quote_is_doubled(string value, string quoted) =>
        Assert.Equal(quoted, Call<string>(Ps, "Quote", value));

    [Fact]
    public void L13_relaunch_arguments_keep_their_spaces_and_quotes()
    {
        IEnumerable<string> args = ["--plain", "two words", "say \"hi\"", @"C:\dir\", ""];
        Assert.Equal(@"--plain ""two words"" ""say \""hi\"""" C:\dir\ """"", Call<string>(Ps, "CommandLine", args));
    }

    /// Runs the real update helper with PowerShell where one is given
    /// (DEADWAX_TEST_PWSH=/path/to/pwsh): the curly quote in the install path
    /// no longer breaks the script, and one in an argument runs nothing.
    [Fact]
    public void L13_the_update_helper_survives_curly_quotes_under_PowerShell()
    {
        var pwsh = Environment.GetEnvironmentVariable("DEADWAX_TEST_PWSH");
        if (string.IsNullOrEmpty(pwsh)) return;
        var root = Directory.CreateTempSubdirectory("deadwax-l13-").FullName;
        try
        {
            var dir = Path.Combine(root, "Dave\x2019s Apps");
            Directory.CreateDirectory(dir);
            var staged = Path.Combine(root, "staged.exe"); File.WriteAllText(staged, "new");
            var target = Path.Combine(dir, "Deadwax.exe"); File.WriteAllText(target, "old");
            var pwned = Path.Combine(root, "PWNED");
            string[] args = [$"x\x2019; Set-Content -Path \"{pwned}\" -Value injected; #"];
            var script = Call<string>(typeof(Deadwax.App.UpdateService), "WindowsApplyScript",
                999999, staged, target, Path.Combine(root, "failed"), root, Path.Combine(root, "stage"), null, Path.Combine(root, "helper"), args);
            var ps1 = Path.Combine(root, "apply.ps1");
            File.WriteAllText(ps1, script);

            var start = new ProcessStartInfo(pwsh) { ArgumentList = { "-NoProfile", "-NonInteractive", "-File", ps1 }, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(start)!)
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.DoesNotContain("ParserError", output.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("new", File.ReadAllText(target));
            Assert.False(File.Exists(pwned));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
