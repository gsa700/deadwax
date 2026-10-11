using System.Buffers.Binary;
using Deadwax.Verify;

namespace Deadwax.Tests;

public class LoudnessTests
{
    /// A 997 Hz tone on both channels, `dbfs` below full scale, for `seconds`.
    private static byte[] Tone(double dbfs, double seconds, double hz = 997)
    {
        var frames = (int)(seconds * 44100);
        var amplitude = Math.Pow(10, dbfs / 20) * 32767;
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var s = (short)Math.Round(Math.Sin(i * 2 * Math.PI * hz / 44100) * amplitude);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), s);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), s);
        }
        return bytes;
    }

    private static LoudnessMeter Measure(params byte[][] parts)
    {
        var meter = new LoudnessMeter();
        foreach (var p in parts) meter.Add(p);
        return meter;
    }

    /// BS.1770's calibration: a 997 Hz sine at 0 dBFS in one channel reads
    /// -3.01 LUFS, so the same tone at -20 dBFS in both reads -20.0.
    [Fact]
    public void Calibration_tone_reads_its_level()
    {
        var meter = Measure(Tone(-20, 20));
        Assert.InRange(meter.Lufs!.Value, -20.05, -19.95);
        Assert.Equal("2.00 dB", LoudnessMeter.Tags(meter, meter.Lufs, meter.Peak)["REPLAYGAIN_TRACK_GAIN"]);
    }

    [Fact]
    public void Fed_in_pieces_reads_the_same_as_all_at_once()
    {
        var audio = Tone(-14, 9.37, 440);
        var whole = Measure(audio);
        var pieces = Measure(audio[..123_456], audio[123_456..777_772], audio[777_772..]);
        Assert.Equal(whole.Lufs, pieces.Lufs);
        Assert.Equal(whole.Peak, pieces.Peak);
    }

    [Fact]
    public void Silence_has_no_loudness_and_gets_no_track_tags()
    {
        var meter = Measure(new byte[44100 * 4 * 5]);
        Assert.Null(meter.Lufs);
        Assert.Empty(LoudnessMeter.Tags(meter, null, 0));
    }

    /// A passage 20 LU under the rest falls below the relative gate and does
    /// not pull the figure down.
    [Fact]
    public void Quiet_passage_is_gated_out()
    {
        var meter = Measure(Tone(-20, 10), Tone(-40, 10));
        Assert.InRange(meter.Lufs!.Value, -20.3, -19.9);
    }

    /// Album loudness gates every block together: tracks at -20 and -30 of
    /// equal length make -22.6, not the -25 an average of the two would give.
    [Fact]
    public void Album_is_measured_over_all_blocks_not_averaged()
    {
        var loud = Measure(Tone(-20, 10));
        var quiet = Measure(Tone(-30, 10));
        Assert.InRange(LoudnessMeter.AlbumLufs([loud, quiet])!.Value, -22.7, -22.5);
    }

    [Fact]
    public void Positive_gain_is_held_back_at_full_scale()
    {
        Assert.Equal(-7.0, LoudnessMeter.Gain(-11, 1.0), 6);                    // negative: never limited
        Assert.Equal(-20 * Math.Log10(0.9), LoudnessMeter.Gain(-25, 0.9), 6);  // +7 would clip a 0.9 peak
        Assert.Equal(7.0, LoudnessMeter.Gain(-25, 0.2), 6);
    }

    /// Found by comparing with rsgain across his library: exact halves round to even.
    [Fact]
    public void Peak_halves_round_to_even_as_printf_does()
    {
        Assert.Equal("0.882812", LoudnessMeter.Fraction(28928 / 32768.0));
        Assert.Equal("0.976562", LoudnessMeter.Fraction(32000 / 32768.0));
        Assert.Equal("0.999969", LoudnessMeter.Fraction(32767 / 32768.0));
    }

    [Fact]
    public void Tags_are_written_as_rsgain_writes_them()
    {
        var meter = Measure(Tone(-6, 5));
        var tags = LoudnessMeter.Tags(meter, meter.Lufs, meter.Peak);
        Assert.Matches(@"^-?\d+\.\d\d dB$", tags["REPLAYGAIN_TRACK_GAIN"]);
        Assert.Matches(@"^\d\.\d{6}$", tags["REPLAYGAIN_ALBUM_PEAK"]);
        Assert.Equal(4, tags.Count);
    }
}
