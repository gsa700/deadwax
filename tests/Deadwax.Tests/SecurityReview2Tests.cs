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

    // ---- the second pass: ripping (L6, L7, L9, L10, L11, L12) ------------------

    private static byte[] Packs(int type, int count, byte fill)
    {
        var packs = new byte[count * CdTextPacks.PackBytes];
        for (var n = 0; n < count; n++)
        {
            var p = packs.AsSpan(n * 18, 18);
            p[0] = (byte)type; p[1] = 0; p[2] = (byte)n; p[3] = 0;
            p.Slice(4, 12).Fill(fill);
        }
        return packs;
    }

    [Theory]
    [InlineData(0x87, 300)]   // binary GENRE packs in one block: 300 x 12 bytes, past the 3073-byte item buffer
    [InlineData(0x80, 300)]   // a TITLE that never ends in NUL
    [InlineData(0x80, 2048)]  // as many packs as the drive may send
    public void L6_cd_text_longer_than_a_block_is_cut_short_not_thrown(int type, int count) =>
        Assert.Null(Record.Exception(() => CdTextPacks.Decode(Packs(type, count, (byte)'A'))));

    /// A disc whose Q frames before track 3 all say "track 3, index 0, absolute time 00:00:00".
    private sealed class LyingQ : IMmcTransport
    {
        public bool SupportsRawSubchannel => false;
        public byte[]? ReadCd(int lsn, int count, bool raw)
        {
            var buffer = new byte[count * (2352 + 16)];
            for (var s = 0; s < count; s++)
                QFrame.EncodePosition(0, 3, 0, 0, 0).CopyTo(buffer, s * (2352 + 16) + 2352);
            return buffer;
        }
        public string? ReadCode(bool isrc, byte track) => null;
        public byte[]? ReadCdTextPacks() => null;
        public void Dispose() { }
    }

    [Fact]
    public void L7_a_pregap_is_never_longer_than_the_track_before_it()
    {
        var pregapBefore = typeof(SubchannelReader).GetMethod("PregapBefore", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pregap = (int)pregapBefore.Invoke(null, [new LyingQ(), false, 3, 20000, 10000, CancellationToken.None])!;
        Assert.InRange(pregap, 0, 10000 - 1);
    }

    [Theory]
    [InlineData("../x-01-01")]
    [InlineData("19x9")]
    [InlineData("")]
    public void L9_no_year_from_a_date_is_anything_but_four_digits(string date)
    {
        var toc = new Toc([new TocTrack(1, 0, true)], 10000);
        var prepared = new PreparedDisc("/dev/none", null, toc, 0, "DISCID", 1u, null, []);
        using var plan = RipSession.PlanSelfDescribed(prepared, new DiscDescription("Artist", "Album", date, ["t"]));
        Assert.True(plan.DefaultYear.All(char.IsAsciiDigit) && plan.DefaultYear.Length == 4, plan.DefaultYear);
        Assert.True(plan.EditionYear is null || plan.EditionYear.All(char.IsAsciiDigit), plan.EditionYear);
        Assert.True(plan.OriginalYear is null || plan.OriginalYear.All(char.IsAsciiDigit), plan.OriginalYear);
        Assert.Equal("1979", LibraryConventions.Year("1979-05-01"));
    }

    [Fact]
    public void L10_a_very_long_title_still_makes_a_file_the_file_system_takes()
    {
        var title = string.Concat(Enumerable.Repeat("Symphonie No. 9 – ", 15)) + "Finale ♪";   // 270+ characters
        var artist = new string('É', 150);
        foreach (var style in Enum.GetValues<TrackStyle>())
        {
            var name = FileNames.Track(style, artist, 1, title);
            Assert.True(Encoding.UTF8.GetByteCount(name) <= FileNames.NameBytes, $"{style}: {Encoding.UTF8.GetByteCount(name)} bytes");
            Assert.EndsWith(".flac", name);
            Assert.DoesNotContain('�', name);
        }
        Assert.True(Encoding.UTF8.GetByteCount(FileNames.Sidecar(artist, title) + ".log") <= FileNames.NameBytes + 4);
        Assert.True(Encoding.UTF8.GetByteCount(FileNames.AlbumLeaf(FolderStyle.ArtistYearAlbum, artist, "1824", title)) <= 255);

        var dir = Directory.CreateTempSubdirectory("deadwax-l10-").FullName;
        try { FlacWriter.Encode(Path.Combine(dir, FileNames.Track(TrackStyle.ArtistNumberTitle, artist, 1, title)), new byte[2352 * 4]); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void L10_names_that_fit_are_left_as_they_are()
    {
        Assert.Equal("Wiener Philharmoniker - 01 - Symphonie No. 9.flac", FileNames.Track(TrackStyle.ArtistNumberTitle, "Wiener Philharmoniker", 1, "Symphonie No. 9"));
        Assert.Equal("café 🎻", FileNames.Shorten("café 🎻 x", Encoding.UTF8.GetByteCount("café 🎻")));
    }

    [Fact]
    public void L11_a_cover_too_large_for_a_flac_is_not_embedded_and_the_rip_goes_on()
    {
        var dir = Directory.CreateTempSubdirectory("deadwax-l11-").FullName;
        try
        {
            var flac = Path.Combine(dir, "01.flac");
            FlacWriter.Encode(flac, new byte[2352 * 4]);
            var cover = new byte[17 * 1024 * 1024]; cover[0] = 0xFF; cover[1] = 0xD8; cover[2] = 0xFF;
            Assert.True(cover.Length < WebLimits.MaxImageBytes);
            File.WriteAllBytes(Path.Combine(dir, "cover.jpg"), cover);
            Assert.Equal(0, CoverEmbed.Apply(dir));
            Assert.False(CoverEmbed.HasFront(flac));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// A PNG of the given size that is only a header: its pixels are never there.
    private static byte[] PngHeader(int width, int height)
    {
        var o = new MemoryStream();
        o.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> n = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(n, data.Length); o.Write(n);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            o.Write(body);
            BinaryPrimitives.WriteUInt32BigEndian(n, System.IO.Hashing.Crc32.HashToUInt32(body)); o.Write(n);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2;   // 8-bit RGB
        Chunk("IHDR", ihdr);
        Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk("IEND", []);
        return o.ToArray();
    }

    [Fact]
    public void L12_a_cover_with_huge_dimensions_is_refused_before_it_is_decoded()
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        Assert.Null(CoverArt.ScaleToFit(PngHeader(20000, 20000), 500));
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 50_000_000);
    }

    [Fact]
    public void L12_a_large_real_cover_is_still_scaled_to_500()
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(3000, 2000));
        surface.Canvas.Clear(SkiaSharp.SKColors.Teal);
        using var shot = surface.Snapshot();
        using var jpeg = shot.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        var scaled = CoverArt.ScaleToFit(jpeg.ToArray(), 500)!;
        using var codec = SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(scaled));
        Assert.Equal((500, 333), (codec.Info.Width, codec.Info.Height));
    }

    // ---- the second pass: the decoder and the comment reader (L1, L2, L3) -----

    /// A real CD track, encoded by Deadwax, with STREAMINFO changed by patch.
    private static string Patched(Action<byte[]> patch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-dec-{Guid.NewGuid():N}.flac");
        FlacWriter.Encode(path, NoiseWithTagAtEnd(4 * 4096, tag: false));
        var bytes = File.ReadAllBytes(path);
        patch(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // STREAMINFO starts at byte 8: min/max block (2+2), min/max frame (3+3),
    // then 20 bits of rate, 3 of channels - 1, 5 of bits - 1, 36 of total samples.
    private static void SetChannels(byte[] b, int channels) => b[8 + 12] = (byte)((b[8 + 12] & 0xF1) | ((channels - 1) << 1));
    private static void SetTotalSamples(byte[] b, long total)
    {
        b[8 + 13] = (byte)((b[8 + 13] & 0xF0) | (int)(total >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 14), (uint)total);
    }

    [Fact]
    public void L1_a_stream_claiming_more_samples_than_deadwax_decodes_is_refused_up_front()
    {
        var path = Patched(b => { SetChannels(b, 8); b[8 + 12] |= 0x01; b[8 + 13] |= 0xF0; SetTotalSamples(b, (1L << 36) - 1); });
        try { Assert.Throws<FlacDecodeException>(() => FlacDecoder.DecodeToPcm(path, out _)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void L1_frames_beyond_what_streaminfo_says_stop_the_decode()
    {
        var path = Patched(b => SetTotalSamples(b, 100));
        try
        {
            var e = Assert.Throws<FlacDecodeException>(() => FlacDecoder.DecodeToPcm(path, out _));
            Assert.Contains("more audio than STREAMINFO", e.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void L2_a_frame_with_other_channels_than_streaminfo_is_a_decode_error_not_a_crash()
    {
        var path = Patched(b => SetChannels(b, 1));
        try { Assert.Throws<FlacDecodeException>(() => FlacDecoder.DecodeToPcm(path, out _)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void L2_an_untouched_file_still_decodes()
    {
        var path = Patched(_ => { });
        try { Assert.Equal(NoiseWithTagAtEnd(4 * 4096, tag: false), FlacDecoder.DecodeToPcm(path, out _)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void L3_reading_many_distinct_tags_is_not_quadratic()
    {
        var path = Patched(_ => { });
        try
        {
            var tags = Enumerable.Range(0, 100_000).Select(i => new Tag($"K{i}", "v")).ToList();
            FlacTags.Replace(path, tags);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var read = FlacTags.Read(path);
            Assert.Equal(100_000, read.Count);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.Elapsed.TotalSeconds:0.0} s");
        }
        finally { File.Delete(path); }
    }
}
