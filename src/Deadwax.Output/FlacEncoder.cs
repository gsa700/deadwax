using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;

namespace Deadwax.Output;

/// Deadwax's own FLAC encoder, written from RFC 9639 (spec §11 step 2,
/// "Step 2 design"). CD audio in (16-bit stereo, 44.1 kHz, little-endian, as
/// ripped), a FLAC stream in the subset out: block size 4096, LPC up to order
/// 8, Rice partitions up to order 8.
///
/// THE SAME BYTES ON EVERY PLATFORM (the hybrid, his call 2026-10-20). libFLAC
/// once chose differently on Windows and Linux because the systems' cos and
/// log round differently (the flac-01 patch). Here nothing calls the
/// platform's maths library: the window is an integer table made from
/// DeterministicMath.Cos (+ and * only), the autocorrelation is summed in
/// 64-bit integers, every residual and every cost is an integer, and only
/// Levinson-Durbin uses doubles, with +, -, * and / alone, in one order. No
/// SIMD on floating point. Frames are independent, so encoding them in
/// parallel changes nothing in the output.
///
/// Every frame is decoded by FlacDecoder as soon as it is made and compared
/// with the samples it came from; a difference stops the encode.
public static class FlacEncoder
{
    public const int BlockSize = 4096;
    private const int MaxLpcOrder = 8;
    private const int MaxPartitionOrder = 8;
    private const int Precision = 12;   // libFLAC's for 16-bit audio in blocks of 2305-4608; 13-15 are tried for the best order

    /// Encodes CD audio to path, with an empty Vorbis comment naming the vendor.
    public static void Encode(string path, ReadOnlySpan<byte> audio, string vendor)
    {
        if (audio.Length % 4 != 0) throw new ArgumentException("CD audio is whole 16-bit stereo samples.", nameof(audio));
        var total = audio.Length / 4;
        var left = new int[total];
        var right = new int[total];
        for (var i = 0; i < total; i++)
        {
            left[i] = BinaryPrimitives.ReadInt16LittleEndian(audio.Slice(i * 4, 2));
            right[i] = BinaryPrimitives.ReadInt16LittleEndian(audio.Slice(i * 4 + 2, 2));
        }
        var md5 = MD5.HashData(audio);

        var blocks = (total + BlockSize - 1) / BlockSize;
        var frames = new byte[blocks][];
        Parallel.For(0, blocks, b => frames[b] = Frame(left, right, b * BlockSize, Math.Min(BlockSize, total - b * BlockSize), b));

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write("fLaC"u8);
        file.Write(StreamInfo(total, frames, md5));
        file.Write(VendorComment(vendor));
        foreach (var f in frames) file.Write(f);
    }

    private static byte[] StreamInfo(int total, byte[][] frames, byte[] md5)
    {
        var w = new BitWriter(64);
        w.Write(0, 1); w.Write(0, 7); w.Write(34, 24);   // not last; STREAMINFO; 34 bytes
        w.Write(BlockSize, 16);
        w.Write(BlockSize, 16);
        w.Write(frames.Length == 0 ? 0u : (uint)frames.Min(f => f.Length), 24);
        w.Write(frames.Length == 0 ? 0u : (uint)frames.Max(f => f.Length), 24);
        w.Write(44100, 20);
        w.Write(2 - 1, 3);
        w.Write(16 - 1, 5);
        w.Write((uint)((long)total >> 32), 4);
        w.Write((uint)total, 32);
        var bytes = w.Bytes.ToArray();
        return [.. bytes, .. md5];
    }

    private static byte[] VendorComment(string vendor)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(vendor);
        var body = new byte[4 + text.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)text.Length);
        text.CopyTo(body, 4);
        return [0x80 | 4, (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];
    }

    // ------------------------------------------------------------------ frames

    private static byte[] Frame(int[] left, int[] right, int start, int n, int number)
    {
        var l = left.AsSpan(start, n).ToArray();
        var r = right.AsSpan(start, n).ToArray();
        var side = new int[n];
        var mid = new int[n];
        for (var i = 0; i < n; i++) { side[i] = l[i] - r[i]; mid[i] = (l[i] + r[i]) >> 1; }

        var pl = Subframe.Best(l, 16);
        var pr = Subframe.Best(r, 16);
        var ps = Subframe.Best(side, 17);
        var pm = Subframe.Best(mid, 16);
        // Independent, left/side, side/right, mid/side: the smallest, the first on a tie.
        var choices = new (int Code, Subframe A, Subframe B)[] { (1, pl, pr), (8, pl, ps), (9, ps, pr), (10, pm, ps) };
        var best = choices[0];
        foreach (var c in choices.Skip(1))
            if (c.A.Bits + c.B.Bits < best.A.Bits + best.B.Bits) best = c;

        var w = new BitWriter(n * 5);
        w.Write(0xFFF8, 16);   // sync, reserved 0, fixed block size
        var sizeCode = n == BlockSize ? 12u : n <= 256 ? 6u : 7u;
        w.Write(sizeCode, 4);
        w.Write(9, 4);         // 44.1 kHz
        w.Write((uint)best.Code, 4);
        w.Write(4, 3);         // 16 bits
        w.Write(0, 1);
        WriteCodedNumber(w, (uint)number);
        if (sizeCode == 6) w.Write((uint)(n - 1), 8);
        else if (sizeCode == 7) w.Write((uint)(n - 1), 16);
        w.Write(FlacCrc.Crc8(w.Bytes), 8);
        best.A.Write(w);
        best.B.Write(w);
        w.AlignToByte();
        w.Write(FlacCrc.Crc16(w.Bytes), 16);
        var frame = w.Bytes.ToArray();

        // Read it back before it counts.
        var decoded = new long[2][];
        FlacDecoder.DecodeFrame(frame, 0, frame.Length, null, ref decoded, out var got);
        if (got != n) throw new OutputException($"FLAC frame {number}: {got} samples decoded, {n} encoded.");
        for (var i = 0; i < n; i++)
            if (decoded[0][i] != l[i] || decoded[1][i] != r[i])
                throw new OutputException($"FLAC frame {number}: sample {i} decodes differently from what was encoded. Not writing this file.");
        return frame;
    }

    /// The frame number as RFC 9639 codes it (the way UTF-8 codes a character).
    private static void WriteCodedNumber(BitWriter w, uint v)
    {
        if (v < 0x80) { w.Write(v, 8); return; }
        var extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : 5;
        var lead = (0xFF00u >> (extra + 1)) & 0xFF;   // 110xxxxx, 1110xxxx, ...
        w.Write(lead | (v >> (6 * extra)), 8);
        for (var i = extra - 1; i >= 0; i--) w.Write(0x80 | ((v >> (6 * i)) & 0x3F), 8);
    }

    // --------------------------------------------------------------- subframes

    private sealed class Subframe
    {
        public long Bits;
        private int _type;          // 0 constant, 1 verbatim, 8+o fixed, 31+o LPC
        private int _bps, _wasted, _order, _precision, _shift;
        private int[] _samples = [];  // after removing wasted bits
        private int[] _coef = [];
        private Residual? _residual;

        public static Subframe Best(int[] x, int bps)
        {
            var n = x.Length;
            if (Array.TrueForAll(x, v => v == x[0]))
                return new Subframe { _type = 0, _bps = bps, _samples = x, Bits = 8 + bps };

            var or = 0;
            foreach (var v in x) or |= v;
            var wasted = BitOperations.TrailingZeroCount(or);
            var s = wasted == 0 ? x : Array.ConvertAll(x, v => v >> wasted);
            var b = bps - wasted;
            long head = 8 + wasted;   // the header byte, and the wasted-bits count in unary

            var best = new Subframe { _type = 1, _bps = b, _wasted = wasted, _samples = s, Bits = head + (long)n * b };
            var residual = new int[n];

            // FIXED, orders 0-4: candidates sized by the cheap estimate, the winner exactly.
            for (var o = 0; o <= Math.Min(4, n - 1); o++)
            {
                if (!FixedResidual(s, o, residual)) continue;
                Consider(ref best, head + (long)o * b + Residual.Estimate(residual, o, n), 8 + o, o, 0, 0, null);
            }

            // LPC, orders 1-8 from the integer autocorrelation.
            if (n > MaxLpcOrder * 2)
            {
                var lp = Lpc.Coefficients(s, Math.Min(MaxLpcOrder, n - 1));
                var bestOrder = 0;
                long bestEstimate = long.MaxValue;
                for (var o = 1; o <= lp.Length; o++)
                {
                    if (lp[o - 1] is not { } c || !Lpc.Quantize(c, Precision, out var q, out var shift) || !LpcResidual(s, q, shift, residual)) continue;
                    var est = head + (long)o * b + 9 + (long)o * Precision + Residual.Estimate(residual, o, n);
                    if (est < bestEstimate) { bestEstimate = est; bestOrder = o; }
                    Consider(ref best, est, 31 + o, o, Precision, shift, q);
                }
                // The best order again at finer coefficient precision.
                if (bestOrder > 0)
                    for (var p = Precision + 1; p <= 15; p++)
                    {
                        if (!Lpc.Quantize(lp[bestOrder - 1]!, p, out var q, out var shift) || !LpcResidual(s, q, shift, residual)) continue;
                        Consider(ref best, head + (long)bestOrder * b + 9 + (long)bestOrder * p + Residual.Estimate(residual, bestOrder, n), 31 + bestOrder, bestOrder, p, shift, q);
                    }
            }

            // The estimate chose; the exact partitioned Rice cost decides against VERBATIM.
            if (best._type != 1)
            {
                var r = best.ResidualOf(s);
                best._residual = Residual.Plan(r, best._order, n);
                var exact = head + (long)best._order * b + (best._type >= 32 ? 9 + (long)best._order * best._precision : 0) + best._residual.Bits;
                if (exact >= (long)n * b + head) return new Subframe { _type = 1, _bps = b, _wasted = wasted, _samples = s, Bits = head + (long)n * b };
                best.Bits = exact;
            }
            return best;

            void Consider(ref Subframe current, long estimate, int type, int order, int precision, int shift, int[]? coef)
            {
                if (estimate >= current.Bits) return;
                current = new Subframe
                {
                    _type = type, _bps = b, _wasted = wasted, _samples = s, _order = order,
                    _precision = precision, _shift = shift, _coef = coef ?? [], Bits = estimate,
                };
            }
        }

        private int[] ResidualOf(int[] s)
        {
            var r = new int[s.Length];
            if (_type >= 32) LpcResidual(s, _coef, _shift, r);
            else FixedResidual(s, _order, r);
            return r;
        }

        public void Write(BitWriter w)
        {
            w.Write(0, 1);
            w.Write((uint)_type, 6);
            w.Write(_wasted > 0 ? 1u : 0u, 1);
            if (_wasted > 0) w.WriteUnary((ulong)(_wasted - 1));
            switch (_type)
            {
                case 0:
                    w.WriteSigned(_samples[0], _bps);
                    return;
                case 1:
                    foreach (var v in _samples) w.WriteSigned(v, _bps);
                    return;
            }
            for (var i = 0; i < _order; i++) w.WriteSigned(_samples[i], _bps);
            if (_type >= 32)
            {
                w.Write((uint)(_precision - 1), 4);
                w.WriteSigned(_shift, 5);
                foreach (var c in _coef) w.WriteSigned(c, _precision);
            }
            _residual!.Write(w);
        }
    }

    /// FIXED predictor residual into r[order..n); false if one does not fit 32 bits.
    private static bool FixedResidual(int[] s, int order, int[] r)
    {
        for (var i = order; i < s.Length; i++)
        {
            long p = order switch
            {
                0 => 0,
                1 => s[i - 1],
                2 => 2L * s[i - 1] - s[i - 2],
                3 => 3L * s[i - 1] - 3L * s[i - 2] + s[i - 3],
                _ => 4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4],
            };
            var e = s[i] - p;
            if (e is > int.MaxValue or < int.MinValue) return false;
            r[i] = (int)e;
        }
        return true;
    }

    /// LPC residual into r[order..n), exactly as the decoder will predict.
    private static bool LpcResidual(int[] s, int[] q, int shift, int[] r)
    {
        var order = q.Length;
        for (var i = order; i < s.Length; i++)
        {
            long sum = 0;
            for (var j = 0; j < order; j++) sum += (long)q[j] * s[i - 1 - j];
            var e = s[i] - (sum >> shift);
            if (e is > int.MaxValue or < int.MinValue) return false;
            r[i] = (int)e;
        }
        return true;
    }

    // ---------------------------------------------------------------- residual

    /// A partitioned Rice coding of one residual, chosen by its exact size.
    private sealed class Residual
    {
        public long Bits;
        private int _order, _partitionOrder, _method;
        private int[] _params = [];   // per partition: a Rice parameter, or -1 - width for an escape
        private int[] _r = [];

        /// The size at partition order 0 with the best of three Rice parameters
        /// around the mean: cheap, integer, the same everywhere. Used to choose
        /// between predictors; the winner is then sized exactly by Plan.
        public static long Estimate(int[] r, int order, int n)
        {
            ulong sum = 0;
            for (var i = order; i < n; i++) sum += BitWriter.Fold(r[i]);
            var count = n - order;
            if (count == 0) return 6 + 4;
            var mean = sum / (ulong)count;
            var k0 = mean == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(mean);
            long best = long.MaxValue;
            for (var k = Math.Max(0, k0 - 1); k <= Math.Min(30, k0 + 1); k++)
            {
                ulong q = 0;
                for (var i = order; i < n; i++) q += BitWriter.Fold(r[i]) >> k;
                var bits = (long)q + (long)count * (k + 1);
                if (bits < best) best = bits;
            }
            return 2 + 4 + 5 + best;
        }

        /// The exact best: every partition order the block allows, both
        /// parameter widths, every parameter, and the escape to raw bits.
        public static Residual Plan(int[] r, int order, int n)
        {
            var maxP = 0;
            while (maxP < MaxPartitionOrder && n % (1 << (maxP + 1)) == 0 && (n >> (maxP + 1)) > order) maxP++;
            var parts = 1 << maxP;

            // The finest partitions: for each, the sum of (u >> k) for every k,
            // and the range of values for the escape width.
            var sums = new ulong[parts, 31];
            var counts = new long[parts];
            var lo = new long[parts];
            var hi = new long[parts];
            for (var p = 0; p < parts; p++)
            {
                var from = p == 0 ? order : p * (n >> maxP);
                var to = (p + 1) * (n >> maxP);
                counts[p] = to - from;
                long min = 0, max = 0;
                for (var i = from; i < to; i++)
                {
                    var u = BitWriter.Fold(r[i]);
                    for (var k = 0; k < 31 && (u >> k) != 0; k++) sums[p, k] += u >> k;
                    if (r[i] < min) min = r[i];
                    if (r[i] > max) max = r[i];
                }
                lo[p] = min; hi[p] = max;
            }

            Residual? best = null;
            for (var level = maxP; level >= 0; level--)
            {
                if (level < maxP)
                {
                    // Pairs of the level below become one partition.
                    var half = 1 << level;
                    var s2 = new ulong[half, 31];
                    var c2 = new long[half]; var l2 = new long[half]; var h2 = new long[half];
                    for (var p = 0; p < half; p++)
                    {
                        for (var k = 0; k < 31; k++) s2[p, k] = sums[2 * p, k] + sums[2 * p + 1, k];
                        c2[p] = counts[2 * p] + counts[2 * p + 1];
                        l2[p] = Math.Min(lo[2 * p], lo[2 * p + 1]);
                        h2[p] = Math.Max(hi[2 * p], hi[2 * p + 1]);
                    }
                    sums = s2; counts = c2; lo = l2; hi = h2;
                }
                for (var method = 0; method <= 1; method++)
                {
                    var paramBits = method == 0 ? 4 : 5;
                    var maxK = method == 0 ? 14 : 30;
                    long bits = 2 + 4;
                    var chosen = new int[1 << level];
                    for (var p = 0; p < chosen.Length; p++)
                    {
                        long partBest = long.MaxValue;
                        for (var k = 0; k <= maxK; k++)
                        {
                            var cost = (long)sums[p, k] + counts[p] * (k + 1);
                            if (cost < partBest) { partBest = cost; chosen[p] = k; }
                            if (sums[p, k] == 0) break;   // larger k only adds bits
                        }
                        var width = Width(lo[p], hi[p]);
                        if (width <= 31 && 5 + counts[p] * width < partBest) { partBest = 5 + counts[p] * width; chosen[p] = -1 - width; }
                        bits += paramBits + partBest;
                    }
                    if (best is null || bits < best.Bits)
                        best = new Residual { Bits = bits, _order = order, _partitionOrder = level, _method = method, _params = chosen, _r = r };
                }
            }
            return best!;
        }

        private static int Width(long lo, long hi)
        {
            if (lo == 0 && hi == 0) return 0;
            static int W(long v) => v >= 0 ? 64 - BitOperations.LeadingZeroCount((ulong)v) + 1 : 64 - BitOperations.LeadingZeroCount((ulong)~v) + 1;
            return Math.Max(W(lo), W(hi));
        }

        public void Write(BitWriter w)
        {
            var n = _r.Length;
            w.Write((uint)_method, 2);
            w.Write((uint)_partitionOrder, 4);
            var paramBits = _method == 0 ? 4 : 5;
            var escape = (1u << paramBits) - 1;
            var size = n >> _partitionOrder;
            for (var p = 0; p < _params.Length; p++)
            {
                var from = p == 0 ? _order : p * size;
                var to = (p + 1) * size;
                var k = _params[p];
                if (k < 0)
                {
                    var width = -1 - k;
                    w.Write(escape, paramBits);
                    w.Write((uint)width, 5);
                    if (width > 0) for (var i = from; i < to; i++) w.WriteSigned(_r[i], width);
                }
                else
                {
                    w.Write((uint)k, paramBits);
                    for (var i = from; i < to; i++) w.WriteRice(_r[i], k);
                }
            }
        }
    }

    // --------------------------------------------------------------------- LPC

    private static class Lpc
    {
        private static readonly ConcurrentDictionary<int, int[]> Windows = new();

        /// Predictor coefficients for every order up to maxOrder (null where
        /// the recursion ended early): the samples windowed by an integer
        /// Tukey(0.5) table, autocorrelated in 64-bit integers, then
        /// Levinson-Durbin in doubles, + - * / only, in one order.
        public static double[]?[] Coefficients(int[] x, int maxOrder)
        {
            var n = x.Length;
            var window = Windows.GetOrAdd(n, TukeyQ15);
            var xw = new long[n];
            for (var i = 0; i < n; i++) xw[i] = (long)x[i] * window[i] >> 15;
            var autoc = new double[maxOrder + 1];
            for (var lag = 0; lag <= maxOrder; lag++)
            {
                long sum = 0;
                for (var i = lag; i < n; i++) sum += xw[i] * xw[i - lag];
                autoc[lag] = sum;   // below 2^53: exact as a double
            }
            var result = new double[]?[maxOrder];
            if (autoc[0] == 0) return result;

            var a = new double[maxOrder + 1];
            var err = autoc[0];
            for (var m = 1; m <= maxOrder; m++)
            {
                var acc = autoc[m];
                for (var j = 1; j < m; j++) acc -= a[j] * autoc[m - j];
                var k = acc / err;
                var next = (double[])a.Clone();
                next[m] = k;
                for (var j = 1; j < m; j++) next[j] = a[j] - k * a[m - j];
                a = next;
                err *= 1 - k * k;
                result[m - 1] = a[1..(m + 1)];
                if (err <= 0) break;
            }
            return result;
        }

        /// Coefficients as precision-bit integers with a right shift of 0-15:
        /// the shift from the largest coefficient's binary exponent (read from
        /// the double's bits, not from a log), rounding errors carried to the
        /// next coefficient, as libFLAC does.
        public static bool Quantize(double[] c, int precision, out int[] q, out int shift)
        {
            q = new int[c.Length];
            shift = 0;
            var cmax = 0.0;
            foreach (var v in c) cmax = Math.Max(cmax, Math.Abs(v));
            if (cmax <= 0 || double.IsNaN(cmax) || double.IsInfinity(cmax)) return false;
            var exponent = (int)((BitConverter.DoubleToInt64Bits(cmax) >> 52) & 0x7FF) - 1022;   // cmax = f * 2^exponent, 0.5 <= f < 1
            shift = Math.Min(15, precision - 1 - exponent);
            if (shift < 0) return false;
            var qmax = (1 << (precision - 1)) - 1;
            var qmin = -(1 << (precision - 1));
            var scale = (double)(1L << shift);
            var error = 0.0;
            for (var j = 0; j < c.Length; j++)
            {
                error += c[j] * scale;
                var v = (long)Math.Round(error, MidpointRounding.AwayFromZero);
                q[j] = (int)Math.Clamp(v, qmin, qmax);
                error -= q[j];
            }
            return true;
        }

        /// Tukey(0.5), libFLAC's default window, as integers scaled to 2^15.
        private static int[] TukeyQ15(int n)
        {
            var w = new int[n];
            Array.Fill(w, 1 << 15);
            var np = (int)(0.25 * n) - 1;   // libFLAC: p/2 * L - 1 with p = 0.5
            if (np > 0)
                for (var i = 0; i <= np; i++)
                {
                    var a = 0.5 - 0.5 * DeterministicMath.Cos(Math.PI * i / np);
                    var b = 0.5 - 0.5 * DeterministicMath.Cos(Math.PI * (i + np) / np);
                    w[i] = (int)Math.Round(a * (1 << 15), MidpointRounding.AwayFromZero);
                    w[n - np - 1 + i] = (int)Math.Round(b * (1 << 15), MidpointRounding.AwayFromZero);
                }
            return w;
        }
    }
}

/// Maths that comes out the same on every platform: + - * / only, which IEEE
/// 754 defines exactly, never the system's own cos. Accurate to about 1e-15,
/// which is all the window needs; what matters is that every machine agrees.
public static class DeterministicMath
{
    private const double HalfPi = Math.PI / 2;

    /// cos(x) for x in [0, 2π].
    public static double Cos(double x)
    {
        if (x > Math.PI) x = 2 * Math.PI - x;   // cos is symmetric about π
        var t = x - HalfPi;                       // cos(x) = -sin(x - π/2), |t| <= π/2
        var t2 = t * t;
        // sin(t) by its series to t^21, nested from the smallest term.
        var s = 1.0 / 51090942171709440000.0;
        s = 1.0 / 121645100408832000.0 - t2 * s;
        s = 1.0 / 355687428096000.0 - t2 * s;
        s = 1.0 / 1307674368000.0 - t2 * s;
        s = 1.0 / 6227020800.0 - t2 * s;
        s = 1.0 / 39916800.0 - t2 * s;
        s = 1.0 / 362880.0 - t2 * s;
        s = 1.0 / 5040.0 - t2 * s;
        s = 1.0 / 120.0 - t2 * s;
        s = 1.0 / 6.0 - t2 * s;
        s = 1.0 - t2 * s;
        return -(t * s);
    }
}
