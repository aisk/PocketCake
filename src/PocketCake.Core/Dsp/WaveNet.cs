using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PocketCake.Core.Dsp;

/// <summary>
/// WaveNet forward pass. Every kernel keeps the per-element accumulation order of
/// the Dart/Rust reference. Two performance transforms are applied, both exact:
/// <list type="bullet">
/// <item>SIMD only ACROSS independent time samples (each lane performs exactly the
/// scalar sequence of multiplies/adds; no FMA contraction).</item>
/// <item>Time tiling: each dilated layer is evaluated block by block (conv, activation,
/// head accumulation, 1x1 + residual) so the working set stays in cache. Blocks run
/// in DESCENDING time order: the causal conv of a block only reads residual samples
/// at or before its own end, none of which has been updated by this layer yet, so the
/// result equals the full-length, layer-at-a-time reference.</item>
/// </list>
/// </summary>
internal static class WaveNet
{
    private const int Block = 1024;

    /// <summary>`y[yOff + j] += w * x[xOff + j]` for j in [0, n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Axpy(double w, double[] x, int xOff, double[] y, int yOff, int n)
    {
        ref var xr = ref MemoryMarshal.GetArrayDataReference(x);
        ref var yr = ref MemoryMarshal.GetArrayDataReference(y);
        var j = 0;

        if (Vector.IsHardwareAccelerated && n >= Vector<double>.Count)
        {
            var wv = new Vector<double>(w);
            var lim = n - Vector<double>.Count;

            for (; j <= lim; j += Vector<double>.Count)
            {
                var xv = Vector.LoadUnsafe(ref xr, (nuint)(xOff + j));
                var yv = Vector.LoadUnsafe(ref yr, (nuint)(yOff + j));

                (yv + wv * xv).StoreUnsafe(ref yr, (nuint)(yOff + j));
            }
        }

        for (; j < n; j++)
        {
            Unsafe.Add(ref yr, yOff + j) += w * Unsafe.Add(ref xr, xOff + j);
        }
    }

    /// <summary>
    /// `out[r][outOff + j] = sum_i m[r][i] * x[i][xOff + j]` for j in [0, n): each
    /// element starts at 0 and accumulates with i ascending (the reference
    /// `_matmulInto` order). Four rows x two time vectors are kept in registers.
    /// </summary>
    private static void MatBlock(double[][] m, double[][] x, int xOff, double[][] @out, int outOff, int n)
    {
        var rows = m.Length;
        var kk = m[0].Length;
        var vc = Vector<double>.Count;
        var r = 0;

        if (Vector.IsHardwareAccelerated)
        {
            for (; r + 4 <= rows; r += 4)
            {
                var m0 = m[r];
                var m1 = m[r + 1];
                var m2 = m[r + 2];
                var m3 = m[r + 3];
                ref var o0 = ref MemoryMarshal.GetArrayDataReference(@out[r]);
                ref var o1 = ref MemoryMarshal.GetArrayDataReference(@out[r + 1]);
                ref var o2 = ref MemoryMarshal.GetArrayDataReference(@out[r + 2]);
                ref var o3 = ref MemoryMarshal.GetArrayDataReference(@out[r + 3]);
                var j = 0;

                for (; j <= n - 2 * vc; j += 2 * vc)
                {
                    var src = (nuint)(xOff + j);
                    var srcB = src + (nuint)vc;
                    Vector<double> a0 = default, b0 = default, a1 = default, b1 = default;
                    Vector<double> a2 = default, b2 = default, a3 = default, b3 = default;

                    for (var i = 0; i < kk; i++)
                    {
                        ref var xr = ref MemoryMarshal.GetArrayDataReference(x[i]);
                        var xa = Vector.LoadUnsafe(ref xr, src);
                        var xb = Vector.LoadUnsafe(ref xr, srcB);
                        var w0 = new Vector<double>(m0[i]);
                        var w1 = new Vector<double>(m1[i]);
                        var w2 = new Vector<double>(m2[i]);
                        var w3 = new Vector<double>(m3[i]);

                        a0 += w0 * xa;
                        b0 += w0 * xb;
                        a1 += w1 * xa;
                        b1 += w1 * xb;
                        a2 += w2 * xa;
                        b2 += w2 * xb;
                        a3 += w3 * xa;
                        b3 += w3 * xb;
                    }

                    var d = (nuint)(outOff + j);
                    var dB = d + (nuint)vc;

                    a0.StoreUnsafe(ref o0, d);
                    b0.StoreUnsafe(ref o0, dB);
                    a1.StoreUnsafe(ref o1, d);
                    b1.StoreUnsafe(ref o1, dB);
                    a2.StoreUnsafe(ref o2, d);
                    b2.StoreUnsafe(ref o2, dB);
                    a3.StoreUnsafe(ref o3, d);
                    b3.StoreUnsafe(ref o3, dB);
                }

                for (; j < n; j++)
                {
                    for (var q = 0; q < 4; q++)
                    {
                        var mq = m[r + q];
                        var acc = 0.0;

                        for (var i = 0; i < kk; i++)
                        {
                            acc += mq[i] * x[i][xOff + j];
                        }

                        @out[r + q][outOff + j] = acc;
                    }
                }
            }
        }

        for (; r < rows; r++)
        {
            var mr = m[r];
            var outr = @out[r];

            Array.Clear(outr, outOff, n);

            for (var i = 0; i < kk; i++)
            {
                Axpy(mr[i], x[i], xOff, outr, outOff, n);
            }
        }
    }

    /// <summary>
    /// Dilated causal conv1d for output times [t0, t0 + n), written to
    /// `out[o][outOff + j]`. Per element: zero, then per tap (ascending)
    /// `+= sum_i k[o][i] * x[i][tt - shift]` (only where tt &gt;= shift), then `+= bias`.
    /// </summary>
    private static void ConvBlock(
        double[][] x, double[][][] kernels, double[] b, int dil, int t0, int n, double[][] @out, int outOff)
    {
        var cout = kernels[0].Length;
        var k = kernels.Length;
        var cin = x.Length;
        var vc = Vector<double>.Count;

        for (var o = 0; o < cout; o++)
        {
            Array.Clear(@out[o], outOff, n);
        }

        for (var ki = 0; ki < k; ki++)
        {
            var kernel = kernels[ki];
            var shift = dil * (k - 1 - ki); // = -offset, >= 0
            var jStart = Math.Max(0, shift - t0); // first j with t0 + j >= shift

            if (jStart >= n)
            {
                continue;
            }

            var srcBase = t0 - shift; // source index = srcBase + j
            var o = 0;

            if (Vector.IsHardwareAccelerated)
            {
                // Four outputs x two time vectors = eight independent accumulation chains
                // (hides add latency). The scalar tail covers the remaining samples.
                for (; o + 4 <= cout; o += 4)
                {
                    ConvTile4(x, kernel, o, srcBase, jStart, n, @out, outOff);
                }
            }

            for (; o < cout; o++)
            {
                var kro = kernel[o];
                ref var outr = ref MemoryMarshal.GetArrayDataReference(@out[o]);
                var j = jStart;

                if (Vector.IsHardwareAccelerated)
                {
                    var lim = n - 2 * vc;

                    // Two independent time vectors per pass to amortize the weight broadcast.
                    for (; j <= lim; j += 2 * vc)
                    {
                        var src = (nuint)(srcBase + j);
                        var acc0 = Vector<double>.Zero;
                        var acc1 = Vector<double>.Zero;

                        for (var i = 0; i < cin; i++)
                        {
                            ref var xr = ref MemoryMarshal.GetArrayDataReference(x[i]);
                            var w = new Vector<double>(kro[i]);

                            acc0 += w * Vector.LoadUnsafe(ref xr, src);
                            acc1 += w * Vector.LoadUnsafe(ref xr, src + (nuint)vc);
                        }

                        var dst = (nuint)(outOff + j);

                        (Vector.LoadUnsafe(ref outr, dst) + acc0).StoreUnsafe(ref outr, dst);
                        (Vector.LoadUnsafe(ref outr, dst + (nuint)vc) + acc1).StoreUnsafe(ref outr, dst + (nuint)vc);
                    }
                }

                for (; j < n; j++)
                {
                    var src = srcBase + j;
                    var acc = 0.0;

                    for (var i = 0; i < cin; i++)
                    {
                        acc += kro[i] * x[i][src];
                    }

                    Unsafe.Add(ref outr, outOff + j) += acc;
                }
            }
        }

        for (var o = 0; o < cout; o++)
        {
            var outo = @out[o];
            var bo = b[o];

            for (var j = 0; j < n; j++)
            {
                outo[outOff + j] += bo;
            }
        }
    }

    /// <summary>
    /// One tap of <see cref="ConvBlock"/> for outputs [o, o + 4) over j in [jStart, n):
    /// `out[q][outOff + j] += sum_i kernel[q][i] * x[i][srcBase + j]` (i ascending).
    /// </summary>
    private static void ConvTile4(
        double[][] x, double[][] kernel, int o, int srcBase, int jStart, int n, double[][] @out, int outOff)
    {
        var vc = Vector<double>.Count;
        var cin = x.Length;
        var k0 = kernel[o];
        var k1 = kernel[o + 1];
        var k2 = kernel[o + 2];
        var k3 = kernel[o + 3];
        ref var o0 = ref MemoryMarshal.GetArrayDataReference(@out[o]);
        ref var o1 = ref MemoryMarshal.GetArrayDataReference(@out[o + 1]);
        ref var o2 = ref MemoryMarshal.GetArrayDataReference(@out[o + 2]);
        ref var o3 = ref MemoryMarshal.GetArrayDataReference(@out[o + 3]);
        var j = jStart;
        var lim = n - 2 * vc;

        for (; j <= lim; j += 2 * vc)
        {
            var src = (nuint)(srcBase + j);
            var srcB = src + (nuint)vc;
            Vector<double> a0 = default, b0 = default, a1 = default, b1 = default;
            Vector<double> a2 = default, b2 = default, a3 = default, b3 = default;

            for (var i = 0; i < cin; i++)
            {
                ref var xr = ref MemoryMarshal.GetArrayDataReference(x[i]);
                var xa = Vector.LoadUnsafe(ref xr, src);
                var xb = Vector.LoadUnsafe(ref xr, srcB);
                var w0 = new Vector<double>(k0[i]);
                var w1 = new Vector<double>(k1[i]);
                var w2 = new Vector<double>(k2[i]);
                var w3 = new Vector<double>(k3[i]);

                a0 += w0 * xa;
                b0 += w0 * xb;
                a1 += w1 * xa;
                b1 += w1 * xb;
                a2 += w2 * xa;
                b2 += w2 * xb;
                a3 += w3 * xa;
                b3 += w3 * xb;
            }

            var d = (nuint)(outOff + j);
            var dB = d + (nuint)vc;

            (Vector.LoadUnsafe(ref o0, d) + a0).StoreUnsafe(ref o0, d);
            (Vector.LoadUnsafe(ref o0, dB) + b0).StoreUnsafe(ref o0, dB);
            (Vector.LoadUnsafe(ref o1, d) + a1).StoreUnsafe(ref o1, d);
            (Vector.LoadUnsafe(ref o1, dB) + b1).StoreUnsafe(ref o1, dB);
            (Vector.LoadUnsafe(ref o2, d) + a2).StoreUnsafe(ref o2, d);
            (Vector.LoadUnsafe(ref o2, dB) + b2).StoreUnsafe(ref o2, dB);
            (Vector.LoadUnsafe(ref o3, d) + a3).StoreUnsafe(ref o3, d);
            (Vector.LoadUnsafe(ref o3, dB) + b3).StoreUnsafe(ref o3, dB);
        }

        for (; j < n; j++)
        {
            var src = srcBase + j;

            for (var q = 0; q < 4; q++)
            {
                var kq = kernel[o + q];
                var acc = 0.0;

                for (var i = 0; i < cin; i++)
                {
                    acc += kq[i] * x[i][src];
                }

                @out[o + q][outOff + j] += acc;
            }
        }
    }

    /// <summary>tanh exactly as the Dart port computes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Tanh(double x)
    {
        if (x > 20.0)
        {
            return 1.0;
        }

        if (x < -20.0)
        {
            return -1.0;
        }

        var e2 = Math.Exp(2.0 * x);

        return (e2 - 1.0) / (e2 + 1.0);
    }

    /// <summary>Runs the WaveNet over <paramref name="mono"/> (48 kHz) and returns the head output.</summary>
    public static double[] Run(double[] mono, NamModel model, CancellationToken ct = default)
    {
        var t = mono.Length;
        double[][] cond = [mono]; // read-only conditioning
        var head = Array.Empty<double[]>();
        var x = cond;

        foreach (var la in model.LayerArrays)
        {
            ct.ThrowIfCancellationRequested();

            var ch = la.Channels;

            if (la.RechW[0].Length != x.Length)
            {
                throw new FormatException("invalid .nam: layer input_size does not match the previous layer");
            }

            var xx = Alloc(ch, t);

            for (var t0 = 0; t0 < t; t0 += Block)
            {
                var n = Math.Min(Block, t - t0);

                MatBlock(la.RechW, x, t0, xx, t0, n);
            }

            x = []; // release the previous array's residual stream

            var acc = Alloc(ch, t);

            for (var i = 0; i < head.Length && i < acc.Length; i++)
            {
                var ai = acc[i];
                var hi = head[i];

                for (var j = 0; j < t; j++)
                {
                    ai[j] += hi[j];
                }
            }

            head = [];

            // Block of conv output, overwritten in place by the post-activation.
            var post = Alloc(ch, Block);
            var mix = Alloc(ch, Block); // input mixin block
            var ow = Alloc(ch, Block); // layer1x1 block

            foreach (var layer in la.Layers)
            {
                ct.ThrowIfCancellationRequested();

                if (layer.MixinW[0].Length != cond.Length)
                {
                    throw new FormatException("invalid .nam: condition_size must be 1");
                }

                var act = layer.Act;

                for (var t1 = t; t1 > 0; t1 -= Block)
                {
                    var t0 = Math.Max(0, t1 - Block);
                    var n = t1 - t0;

                    ConvBlock(xx, layer.ConvW, layer.ConvB, layer.Dil, t0, n, post, 0);
                    MatBlock(layer.MixinW, cond, t0, mix, 0, n);

                    for (var c = 0; c < ch; c++)
                    {
                        var row = mix[c];
                        var pc = post[c];
                        var ac = acc[c];

                        if (act.Kind == ActKind.Tanh)
                        {
                            for (var j = 0; j < n; j++)
                            {
                                var p = Tanh(pc[j] + row[j]);

                                pc[j] = p;
                                ac[t0 + j] += p;
                            }
                        }
                        else
                        {
                            var slope = act.Slope;

                            for (var j = 0; j < n; j++)
                            {
                                var v = pc[j] + row[j];
                                var p = v >= 0.0 ? v : slope * v;

                                pc[j] = p;
                                ac[t0 + j] += p;
                            }
                        }
                    }

                    // layer1x1, folded into the residual.
                    MatBlock(layer.OutW, post, 0, ow, 0, n);

                    for (var c = 0; c < ch; c++)
                    {
                        var row = ow[c];
                        var xc = xx[c];
                        var bc = layer.OutB[c];

                        for (var j = 0; j < n; j++)
                        {
                            xc[t0 + j] += row[j] + bc;
                        }
                    }
                }
            }

            var headOut = Alloc(la.HrW[0].Length, t);

            for (var t0 = 0; t0 < t; t0 += Block)
            {
                ConvBlock(acc, la.HrW, la.HrB, 1, t0, Math.Min(Block, t - t0), headOut, t0);
            }

            x = xx;
            head = headOut;
        }

        var h0 = head[0];
        var outp = new double[t];

        for (var j = 0; j < t; j++)
        {
            outp[j] = model.HeadScale * h0[j];
        }

        return outp;
    }

    private static double[][] Alloc(int rows, int t)
    {
        var a = new double[rows][];

        for (var i = 0; i < rows; i++)
        {
            a[i] = new double[t];
        }

        return a;
    }
}
