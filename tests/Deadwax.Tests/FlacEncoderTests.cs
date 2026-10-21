using System.Buffers.Binary;
using System.Security.Cryptography;
using Deadwax.Output;
using Deadwax.Verify;

namespace Deadwax.Tests;

/// Deadwax's own FLAC encoder and decoder (spec §11 step 2): every kind of
/// signal round-trips exactly, and the same input makes the same bytes on
/// every platform (the hash test, run by CI on Linux, Windows and macOS).
public class FlacEncoderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deadwax-flacenc-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// A generator that is the same everywhere (System.Random's algorithm is
    /// not promised to stay the same).
    private sealed class Lcg(uint seed)
    {
        private uint _s = seed;
        public int Next(int range) { _s = _s * 1664525u + 1013904223u; return (int)((_s >> 8) % (uint)range); }
    }

    private static byte[] Pcm(int frames, Func<int, (int L, int R)> sample)
    {
        var b = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = sample(i);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 4), (short)Math.Clamp(l, short.MinValue, short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 4 + 2), (short)Math.Clamp(r, short.MinValue, short.MaxValue));
        }
        return b;
    }

    private byte[] RoundTrip(byte[] pcm)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.flac");
        FlacEncoder.Encode(path, pcm, "Deadwax test");
        var back = FlacDecoder.DecodeToPcm(path, out var info);   // checks both CRCs and the MD5
        Assert.Equal(pcm, back);
        Assert.Equal(AudioChecks.Md5(pcm), FlacInfo.AudioMd5(path));
        Assert.Equal((44100, 2, 16, (long)pcm.Length / 4), (info.SampleRate, info.Channels, info.BitsPerSample, info.TotalSamples));
        return File.ReadAllBytes(path);
    }

    public static TheoryData<string> Signals => ["silence", "one sample", "a frame and one sample", "short", "tone", "noise",
        "full-scale square", "wasted bits", "mono in stereo", "opposite channels", "music-like"];

    [Theory]
    [MemberData(nameof(Signals))]
    public void Every_kind_of_signal_round_trips_exactly(string signal)
    {
        var rng = new Lcg(7);
        var pcm = signal switch
        {
            "silence" => Pcm(10000, _ => (0, 0)),
            "one sample" => Pcm(1, _ => (1234, -5678)),
            "a frame and one sample" => Pcm(FlacEncoder.BlockSize + 1, i => (i % 300 - 150, -(i % 77))),
            "short" => Pcm(100, i => (i * 50, -i * 30)),
            "tone" => Pcm(20000, i => ((int)(12000 * Math.Sin(i * 0.03)), (int)(9000 * Math.Sin(i * 0.05)))),
            "noise" => Pcm(20000, _ => (rng.Next(65536) - 32768, rng.Next(65536) - 32768)),
            "full-scale square" => Pcm(20000, i => (i / 50 % 2 == 0 ? short.MaxValue : short.MinValue, i / 37 % 2 == 0 ? short.MinValue : short.MaxValue)),
            "wasted bits" => Pcm(20000, i => ((i % 512 - 256) * 8, (i % 300 - 150) * 4)),
            "mono in stereo" => Pcm(20000, i => { var v = (int)(10000 * Math.Sin(i * 0.01)) + rng.Next(64); return (v, v); }),
            "opposite channels" => Pcm(20000, i => { var v = (int)(10000 * Math.Sin(i * 0.02)); return (v, -v); }),
            _ => Pcm(50000, i => ((int)(8000 * Math.Sin(i * 0.013) + 3000 * Math.Sin(i * 0.11)) + rng.Next(200),
                                  (int)(7000 * Math.Sin(i * 0.017) + 2500 * Math.Sin(i * 0.09)) + rng.Next(200))),
        };
        RoundTrip(pcm);
    }

    [Fact]
    public void Silence_and_mono_compress_to_almost_nothing()
    {
        var silence = RoundTrip(Pcm(44100, _ => (0, 0)));
        Assert.True(silence.Length < 400, $"{silence.Length} bytes for a second of silence");
        var rng = new Lcg(3);
        var mono = RoundTrip(Pcm(44100, i => { var v = (int)(10000 * Math.Sin(i * 0.01)) + rng.Next(64); return (v, v); }));
        var stereo = RoundTrip(Pcm(44100, i => ((int)(10000 * Math.Sin(i * 0.01)) + rng.Next(64), (int)(10000 * Math.Sin(i * 0.013)) + rng.Next(64))));
        Assert.True(mono.Length < stereo.Length * 0.7, $"left = right should cost about one channel ({mono.Length} vs {stereo.Length})");
    }

    /// THE SAME BYTES ON EVERY PLATFORM. If this fails on one CI runner and
    /// not the others, something in the encoder went through the platform's
    /// maths (the trap the flac-01 patch fixed for libFLAC). The hash was made
    /// on the Mac (osx-arm64) on 2026-10-20; Linux and Windows must agree.
    [Fact]
    public void The_same_input_makes_the_same_bytes_everywhere()
    {
        var rng = new Lcg(2026);
        var pcm = Pcm(3 * FlacEncoder.BlockSize + 1234, i =>
            ((int)(9000 * Math.Sin(i * 0.021) + 4000 * Math.Sin(i * 0.0071)) + rng.Next(300) - 150,
             (int)(8000 * Math.Sin(i * 0.019 + 1) + 3000 * Math.Sin(i * 0.0053)) + rng.Next(300) - 150));
        // The input is made with Math.Sin, which may differ in the last bit
        // between platforms, so it is fixed by its own hash first.
        Assert.Equal(InputHash, Convert.ToHexStringLower(SHA256.HashData(pcm)));
        var bytes = RoundTrip(pcm);
        Assert.Equal(OutputHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private const string InputHash = "aa3d252dce414c1addd646c4b7f973ccad24e354011e3d90406b9a7632f14fab";
    private const string OutputHash = "d00b5b2fe3ff8338510f964aa96af7ab9bda7a25d778c5e04314f0f02e04cf33";

    [Fact]
    public void DeterministicMath_cos_is_accurate()
    {
        for (var i = 0; i <= 1000; i++)
        {
            var x = 2 * Math.PI * i / 1000;
            Assert.True(Math.Abs(DeterministicMath.Cos(x) - Math.Cos(x)) < 1e-14, $"cos({x})");
        }
    }
}
