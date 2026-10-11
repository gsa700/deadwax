using System.Globalization;

namespace Deadwax.Verify;

/// ReplayGain 2.0, measured here rather than by a tool the user would have to
/// install (2026-10-11: a downloaded Deadwax rips with full function).
///
/// Loudness is ITU-R BS.1770 / EBU R128 integrated loudness: K-weighting (a
/// high shelf for the head, a high-pass for the ear's bass roll-off), mean
/// square power in 400 ms blocks every 100 ms, an absolute gate at -70 LUFS and
/// a relative gate 10 LU under what survives it. Album loudness gates every
/// block of every track together; it is not an average of the track figures.
/// Gain is the step to -18 LUFS, ReplayGain 2.0's reference (its old 89 dB).
///
/// The figures are meant to be rsgain's to the hundredth of a dB (rsgain easy:
/// sample peak, clipping prevented only for positive gains). rsgain is the
/// check during development, never a dependency.
public sealed class LoudnessMeter
{
    public const double ReferenceLufs = -18.0;
    private const int Rate = 44100;
    private const int StepFrames = Rate / 10;          // 100 ms
    private static readonly double AbsoluteGate = Math.Pow(10, (-70.0 + 0.691) / 10);

    private readonly Biquad[] _left = KWeighting();
    private readonly Biquad[] _right = KWeighting();
    private readonly List<double> _steps = [];          // filtered energy per 100 ms
    private double _stepEnergy;
    private int _stepFrames;
    private int _peak;

    /// 16-bit little-endian stereo at 44.1 kHz, as read from the disc. May be
    /// fed in pieces; a track is one meter.
    public void Add(ReadOnlySpan<byte> audio)
    {
        for (var i = 0; i + 3 < audio.Length; i += 4)
        {
            int l = (short)(audio[i] | audio[i + 1] << 8), r = (short)(audio[i + 2] | audio[i + 3] << 8);
            _peak = Math.Max(_peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            var fl = Filter(_left, l / 32768.0);
            var fr = Filter(_right, r / 32768.0);
            _stepEnergy += fl * fl + fr * fr;
            if (++_stepFrames == StepFrames)
            {
                _steps.Add(_stepEnergy);
                _stepEnergy = 0;
                _stepFrames = 0;
            }
        }
    }

    /// Largest sample magnitude as a fraction of full scale.
    public double Peak => _peak / 32768.0;

    /// Mean square of each whole 400 ms block. A part-block at the end is not
    /// measured, as in libebur128.
    public IEnumerable<double> Blocks()
    {
        for (var i = 0; i + 3 < _steps.Count; i++)
            yield return (_steps[i] + _steps[i + 1] + _steps[i + 2] + _steps[i + 3]) / (4.0 * StepFrames);
    }

    /// Integrated loudness in LUFS; null when nothing passes the gates (silence).
    public double? Lufs => Integrated(Blocks());

    /// The album's: every track's blocks gated together.
    public static double? AlbumLufs(IEnumerable<LoudnessMeter> tracks) => Integrated(tracks.SelectMany(t => t.Blocks()));

    private static double? Integrated(IEnumerable<double> blocks)
    {
        var loud = blocks.Where(b => b >= AbsoluteGate).ToList();
        if (loud.Count == 0) return null;
        var relative = loud.Average() * 0.1;              // -10 LU
        var kept = loud.Where(b => b >= relative).ToList();
        return kept.Count == 0 ? null : -0.691 + 10 * Math.Log10(kept.Average());
    }

    /// Gain to the reference, held back so a positive gain never lifts the
    /// peak over full scale.
    public static double Gain(double lufs, double peak)
    {
        var gain = ReferenceLufs - lufs;
        if (gain > 0 && peak > 0 && peak * Math.Pow(10, gain / 20) > 1.0) gain = -20 * Math.Log10(peak);
        return gain;
    }

    /// The four tags, as rsgain writes them ("-9.71 dB", "0.999969"). A track
    /// with no measurable loudness gets album tags only.
    public static Dictionary<string, string> Tags(LoudnessMeter track, double? albumLufs, double albumPeak)
    {
        var tags = new Dictionary<string, string>();
        if (track.Lufs is { } t)
        {
            tags["REPLAYGAIN_TRACK_GAIN"] = Db(Gain(t, track.Peak));
            tags["REPLAYGAIN_TRACK_PEAK"] = Fraction(track.Peak);
        }
        if (albumLufs is { } a)
        {
            tags["REPLAYGAIN_ALBUM_GAIN"] = Db(Gain(a, albumPeak));
            tags["REPLAYGAIN_ALBUM_PEAK"] = Fraction(albumPeak);
        }
        return tags;
    }

    private static string Db(double gain) => (Math.Round(gain, 2) == 0 ? 0 : gain).ToString("0.00", CultureInfo.InvariantCulture) + " dB";
    // A peak of n/32768 can land exactly halfway (28928/32768 = 0.8828125);
    // printf, and so rsgain, rounds that to even, where .NET's format rounds up.
    public static string Fraction(double peak) =>
        Math.Round(peak, 6, MidpointRounding.ToEven).ToString("0.000000", CultureInfo.InvariantCulture);

    private static double Filter(Biquad[] stages, double x)
    {
        foreach (var s in stages) x = s.Run(x);
        return x;
    }

    /// BS.1770's two stages, designed for the sample rate the way libebur128
    /// does it (the standard prints coefficients for 48 kHz only).
    private static Biquad[] KWeighting()
    {
        // Stage 1: high shelf, about +4 dB above 1.5 kHz.
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / Rate);
        var vh = Math.Pow(10, g / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        var shelf = new Biquad(
            (vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
            2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);

        // Stage 2: high-pass at about 38 Hz.
        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / Rate);
        a0 = 1 + k / q + k * k;
        var highPass = new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        return [shelf, highPass];
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1, _z2;

        public double Run(double x)
        {
            var y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
