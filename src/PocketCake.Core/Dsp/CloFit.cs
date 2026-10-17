namespace PocketCake.Core.Dsp;

internal readonly record struct NlGains(double PosMax, double NegMax, double PosRate, double NegRate);

// Welch-averaged Wiener FIR, min-phase design and the multi-level
// Wiener-Hammerstein fit.
public static partial class CloDsp
{
    private const int Nfft = 8192;
    private const int Bins = Nfft / 2 + 1;
    private const int WelchHop = Nfft / 4;

    private const double Pow2Neg = 0.9438743126816935; // 2^(-0.5/6)
    private const double Pow2Pos = 1.0594630943592953; // 2^(0.5/6)

    private static readonly Fft SharedFft = new(Nfft);
    private static readonly double[] HannWin = MakeHann();

    private static double[] MakeHann()
    {
        var w = new double[Nfft];

        for (var i = 0; i < Nfft; i++)
        {
            w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / Nfft);
        }

        return w;
    }

    /// <summary>
    /// Runs <paramref name="compute"/> for every frame in parallel batches (each with
    /// its own <paramref name="buffers"/> scratch arrays of length N), then calls
    /// <paramref name="accumulate"/> for the batch strictly in ascending frame order.
    /// Keeps any order-dependent accumulation bit-identical to a sequential loop.
    /// </summary>
    private static void ForEachFrameOrdered(
        int frameCount, int buffers, Action<int, double[][]> compute, Action<int, double[][]> accumulate)
    {
        var batch = Math.Max(1, Math.Min(frameCount, Environment.ProcessorCount * 4));
        var slots = new double[batch][][];

        for (var s = 0; s < batch; s++)
        {
            slots[s] = new double[buffers][];

            for (var b = 0; b < buffers; b++)
            {
                slots[s][b] = new double[Nfft];
            }
        }

        for (var first = 0; first < frameCount; first += batch)
        {
            var n = Math.Min(batch, frameCount - first);
            var f0 = first;

            if (n == 1)
            {
                compute(f0, slots[0]);
            }
            else
            {
                Parallel.For(0, n, s => compute(f0 + s, slots[s]));
            }

            for (var s = 0; s < n; s++)
            {
                accumulate(f0 + s, slots[s]);
            }
        }
    }

    /// <summary>
    /// Real inverse FFT: rebuilds the Hermitian full spectrum from N/2+1 bins and
    /// inverse-transforms it. Returns N real samples, UNNORMALIZED.
    /// </summary>
    private static double[] Irfft(double[] re, double[] im)
    {
        var fr = new double[Nfft];
        var fi = new double[Nfft];

        for (var k = 0; k < Bins; k++)
        {
            fr[k] = re[k];
            fi[k] = im[k];
        }

        for (var k = 1; k < Nfft / 2; k++)
        {
            fr[Nfft - k] = re[k];
            fi[Nfft - k] = -im[k];
        }

        SharedFft.Transform(fr, fi, true);

        return fr;
    }

    /// <summary>
    /// Welch-averaged cross/auto spectra over several (x, y) pairs:
    /// `H = sum(Y * conj(X)) / (sum(|X|^2) + reg)`.
    /// </summary>
    private static (double[] Re, double[] Im) WelchPairs(IReadOnlyList<(double[] X, double[] Y)> pairs, double reg)
    {
        var win = HannWin;

        // Frames in reference order: pairs in sequence, hop-spaced within each pair.
        var frames = new List<(int Pair, int Start)>();

        for (var p = 0; p < pairs.Count; p++)
        {
            var lim = Math.Min(pairs[p].X.Length, pairs[p].Y.Length);

            for (var start = 0; start + Nfft <= lim; start += WelchHop)
            {
                frames.Add((p, start));
            }
        }

        var sxyRe = new double[Bins];
        var sxyIm = new double[Bins];
        var sxx = new double[Bins];

        // FFTs run in parallel per batch; the spectra are then accumulated strictly in
        // frame order, so the sums are bit-identical to the sequential reference.
        ForEachFrameOrdered(
            frames.Count,
            4,
            (frame, buf) =>
            {
                var (p, start) = frames[frame];
                var (x, y) = pairs[p];
                var (xr, xi, yr, yi) = (buf[0], buf[1], buf[2], buf[3]);

                for (var i = 0; i < Nfft; i++)
                {
                    xr[i] = x[start + i] * win[i];
                    xi[i] = 0.0;
                    yr[i] = y[start + i] * win[i];
                    yi[i] = 0.0;
                }

                SharedFft.Transform(xr, xi, false);
                SharedFft.Transform(yr, yi, false);
            },
            (_, buf) =>
            {
                var (xr, xi, yr, yi) = (buf[0], buf[1], buf[2], buf[3]);

                for (var i = 0; i < Bins; i++)
                {
                    var axr = xr[i];
                    var axi = xi[i];
                    var ayr = yr[i];
                    var ayi = yi[i];

                    // Y * conj(X)
                    sxyRe[i] += ayr * axr + ayi * axi;
                    sxyIm[i] += ayi * axr - ayr * axi;
                    sxx[i] += axr * axr + axi * axi;
                }
            });

        var sxxMax = 0.0;

        for (var i = 0; i < Bins; i++)
        {
            if (sxx[i] > sxxMax)
            {
                sxxMax = sxx[i];
            }
        }

        var eps = reg * sxxMax;
        var hRe = new double[Bins];
        var hIm = new double[Bins];

        for (var i = 0; i < Bins; i++)
        {
            var den = sxx[i] + eps;

            hRe[i] = sxyRe[i] / den;
            hIm[i] = sxyIm[i] / den;
        }

        return (hRe, hIm);
    }

    /// <summary>First <paramref name="nTaps"/> of the impulse response of spectrum <paramref name="h"/>.</summary>
    private static double[] Firify((double[] Re, double[] Im) h, int nTaps)
    {
        var ir = Irfft(h.Re, h.Im);
        var outp = new double[nTaps];

        for (var i = 0; i < nTaps; i++)
        {
            outp[i] = ir[i] / Nfft;
        }

        return outp;
    }

    /// <summary>
    /// Min-phase FIR whose magnitude response matches <paramref name="mag"/> (real
    /// cepstrum method: log-magnitude -> causal cepstral window -> exp).
    /// </summary>
    private static double[] MinphaseMag(double[] mag, int nTaps)
    {
        var re = new double[Nfft];
        var im = new double[Nfft];

        for (var i = 0; i < Nfft; i++)
        {
            var bin = i < Bins ? i : Nfft - i;

            re[i] = Math.Log(Math.Max(mag[bin], 1e-9));
            im[i] = 0.0;
        }

        SharedFft.Transform(re, im, true); // cepstrum (unnormalized)

        for (var i = 0; i < Nfft; i++)
        {
            double w;

            if (i == 0 || i == Nfft / 2)
            {
                w = 1.0;
            }
            else if (i < Nfft / 2)
            {
                w = 2.0;
            }
            else
            {
                w = 0.0;
            }

            re[i] = re[i] / Nfft * w;
            im[i] = 0.0;
        }

        SharedFft.Transform(re, im, false);

        for (var i = 0; i < Nfft; i++)
        {
            var ex = Math.Exp(re[i]);

            re[i] = ex * Math.Cos(im[i]);
            im[i] = ex * Math.Sin(im[i]);
        }

        SharedFft.Transform(re, im, true);

        var outp = new double[nTaps];

        for (var i = 0; i < nTaps; i++)
        {
            outp[i] = re[i] / Nfft;
        }

        return outp;
    }

    /// <summary>Smooths a magnitude spectrum over ~1/6 octave (log-frequency).</summary>
    private static double[] Logsmooth(double[] mag)
    {
        var outp = new double[Bins];

        outp[0] = mag[0];

        for (var i = 1; i < Bins; i++)
        {
            double f = i;
            var rawLo = (int)(f * Pow2Neg);
            var hi = Math.Min((int)(f * Pow2Pos) + 1, Bins);
            var lo = Math.Min(Math.Max(rawLo, 1), hi - 1);
            var sum = 0.0;

            for (var j = lo; j < hi; j++)
            {
                sum += mag[j];
            }

            outp[i] = sum / (hi - lo);
        }

        return outp;
    }

    /// <summary>
    /// Regularized frequency-domain deconvolution of <paramref name="y"/> by FIR
    /// <paramref name="b"/> (overlap-add, Hann analysis + synthesis).
    /// </summary>
    private static double[] Deconv(double[] y, double[] b, double reg)
    {
        var win = HannWin;
        var scrRe = new double[Nfft];
        var scrIm = new double[Nfft];

        for (var i = 0; i < b.Length && i < Nfft; i++)
        {
            scrRe[i] = b[i];
        }

        SharedFft.Transform(scrRe, scrIm, false);

        var hmax = 0.0;

        for (var i = 0; i < Bins; i++)
        {
            var ns = scrRe[i] * scrRe[i] + scrIm[i] * scrIm[i];

            if (ns > hmax)
            {
                hmax = ns;
            }
        }

        var invRe = new double[Bins];
        var invIm = new double[Bins];

        for (var i = 0; i < Bins; i++)
        {
            var den = scrRe[i] * scrRe[i] + scrIm[i] * scrIm[i] + reg * hmax;

            invRe[i] = scrRe[i] / den;
            invIm[i] = -scrIm[i] / den;
        }

        var outp = new double[y.Length];
        var norm = new double[y.Length];
        var frameCount = y.Length >= Nfft ? (y.Length - Nfft) / WelchHop + 1 : 0;

        // Per-frame filtering runs in parallel; the overlap-add is applied strictly in
        // frame order (bit-identical to the sequential reference).
        ForEachFrameOrdered(
            frameCount,
            2,
            (frame, buf) =>
            {
                var start = frame * WelchHop;
                var (yr, yi) = (buf[0], buf[1]);

                for (var i = 0; i < Nfft; i++)
                {
                    yr[i] = y[start + i] * win[i];
                    yi[i] = 0.0;
                }

                SharedFft.Transform(yr, yi, false);

                // Multiply the N/2+1 unique bins by the inverse filter, then restore the
                // Hermitian upper half before the inverse transform.
                for (var i = 0; i < Bins; i++)
                {
                    var ar = yr[i];
                    var ai = yi[i];

                    yr[i] = ar * invRe[i] - ai * invIm[i];
                    yi[i] = ar * invIm[i] + ai * invRe[i];
                }

                for (var k = 1; k < Nfft / 2; k++)
                {
                    yr[Nfft - k] = yr[k];
                    yi[Nfft - k] = -yi[k];
                }

                SharedFft.Transform(yr, yi, true);
            },
            (frame, buf) =>
            {
                var start = frame * WelchHop;
                var yr = buf[0];

                for (var i = 0; i < Nfft; i++)
                {
                    outp[start + i] += yr[i] / Nfft * win[i];
                    norm[start + i] += win[i] * win[i];
                }
            });

        for (var i = 0; i < outp.Length; i++)
        {
            outp[i] /= Math.Max(norm[i], 1e-3);
        }

        return outp;
    }

    /// <summary>The pedal's asymmetric exponential saturator.</summary>
    private static double[] NlFwd(double[] v, NlGains g)
    {
        var outp = new double[v.Length];

        for (var i = 0; i < v.Length; i++)
        {
            var x = v[i];

            outp[i] = x > 0.0
                ? g.PosMax * (1.0 - Math.Exp(Math.Max(-g.PosRate * x, -60.0)))
                : -g.NegMax * (1.0 - Math.Exp(Math.Min(g.NegRate * x, 60.0)));
        }

        return outp;
    }

    /// <summary>Fits the saturator to per-level (pre-drive -> target output) pairs.</summary>
    private static NlGains FitNl(double[][] vs, double[][] ts)
    {
        const int stride = 16;

        var vq = vs[^1];
        var mags = new List<double>(vq.Length / stride + 1);

        for (var i = 0; i < vq.Length; i += stride)
        {
            var a = Math.Abs(vq[i]);

            if (a > 1e-8)
            {
                mags.Add(a);
            }
        }

        mags.Sort();

        var idx = mags.Count * 9 / 10;
        var p90 = idx < mags.Count ? mags[idx] : 1e-3;
        var rMax = Math.Max(1.0 / Math.Max(p90, 1e-9), 1.0);
        const double rMin = 0.5;

        (double C, double R) FitSide(double sign)
        {
            // per level: (v samples, t samples, weight)
            var xsl = new double[vs.Length][];
            var ysl = new double[vs.Length][];
            var wl = new double[vs.Length];

            for (var li = 0; li < vs.Length; li++)
            {
                var v = vs[li];
                var t = ts[li];
                var lim = Math.Min(v.Length, t.Length);
                var xs = new List<double>();
                var ys = new List<double>();

                for (var i = 0; i < lim; i += stride)
                {
                    var x = v[i] * sign;

                    if (x > 1e-8)
                    {
                        xs.Add(x);
                        ys.Add(t[i] * sign);
                    }
                }

                var sumsq = 0.0;

                foreach (var yv in ys)
                {
                    sumsq += yv * yv;
                }

                var rms = Math.Sqrt(sumsq / Math.Max(ys.Count, 1));

                xsl[li] = xs.ToArray();
                ysl[li] = ys.ToArray();
                wl[li] = 1.0 / Math.Max(rms, 1e-9);
            }

            const int n = 80;
            var gridR = new double[n];
            var gridC = new double[n];
            var gridResid = new double[n];
            var gridOk = new bool[n];

            // Grid points are independent: evaluate them in parallel, then pick the
            // best in ascending order (same strict `<` tie-break as the reference).
            Parallel.For(0, n, kk =>
            {
                var r = rMin * Math.Pow(rMax / rMin, (double)kk / (n - 1));
                var num = 0.0;
                var den = 0.0;

                for (var li = 0; li < xsl.Length; li++)
                {
                    var xs = xsl[li];
                    var ys = ysl[li];
                    var w = wl[li];

                    for (var s = 0; s < xs.Length; s++)
                    {
                        var e = 1.0 - Math.Exp(Math.Max(-r * xs[s], -60.0));

                        num += w * ys[s] * e;
                        den += w * e * e;
                    }
                }

                if (den < 1e-12)
                {
                    return;
                }

                var c = num / den;

                if (c <= 0.0)
                {
                    return;
                }

                var resid = 0.0;

                for (var li = 0; li < xsl.Length; li++)
                {
                    var xs = xsl[li];
                    var ys = ysl[li];
                    var w = wl[li];
                    var se = 0.0;

                    for (var s = 0; s < xs.Length; s++)
                    {
                        var e = 1.0 - Math.Exp(Math.Max(-r * xs[s], -60.0));
                        var d = ys[s] - c * e;

                        se += d * d;
                    }

                    resid += w * se / Math.Max(xs.Length, 1);
                }

                gridR[kk] = r;
                gridC[kk] = c;
                gridResid[kk] = resid;
                gridOk[kk] = true;
            });

            var bestResid = double.PositiveInfinity;
            var bestC = 1.0;
            var bestR = 1.0;

            for (var kk = 0; kk < n; kk++)
            {
                if (gridOk[kk] && gridResid[kk] < bestResid)
                {
                    bestResid = gridResid[kk];
                    bestC = gridC[kk];
                    bestR = gridR[kk];
                }
            }

            return (bestC, bestR);
        }

        var (posmax, posRate) = FitSide(1.0);
        var (negmax, negRate) = FitSide(-1.0);

        return new NlGains(posmax, negmax, posRate, negRate);
    }

    /// <summary>Direct FIR convolution (kernel &lt;= 128 taps).</summary>
    private static double[] ConvFir(double[] x, double[] h)
    {
        var y = new double[x.Length];

        for (var k = 0; k < h.Length && k < x.Length; k++)
        {
            var hk = h[k];

            if (hk == 0.0)
            {
                continue;
            }

            // y[t] += hk * x[t - k] for t in [k, len)
            WaveNet.Axpy(hk, x, 0, y, k, x.Length - k);
        }

        return y;
    }

    /// <summary>
    /// The multi-level fit: probe responses come in as (level, response) pairs over
    /// [0, RUN_LEN); returns (arrayA48[128], arrayB48[2048], gains).
    /// </summary>
    private static (double[] A48, double[] B48, NlGains Gains) FitWhMultilevel(
        double[] di48, IReadOnlyList<(double Level, double[] Response)> respByLevel)
    {
        double[] Seg(double[] x)
        {
            var end = Math.Min(FitEnd, x.Length);

            if (end <= FitStart)
            {
                throw new InvalidOperationException(
                    $"signal too short for the fit: {x.Length} samples (need > {FitStart})");
            }

            return x[FitStart..end];
        }

        double[] RespOf(double lvl)
        {
            foreach (var (l, r) in respByLevel)
            {
                if (l == lvl)
                {
                    return r;
                }
            }

            throw new InvalidOperationException($"probe level missing: {lvl}");
        }

        double[] ScaledSeg(double[] x, double k)
        {
            var s = Seg(x);

            for (var i = 0; i < s.Length; i++)
            {
                s[i] *= k;
            }

            return s;
        }

        var quiet = FitLevels[^1];
        var xsFit = new double[FitLevels.Length][];
        var ysFit = new double[FitLevels.Length][];

        for (var i = 0; i < FitLevels.Length; i++)
        {
            xsFit[i] = ScaledSeg(di48, FitLevels[i]);
            ysFit[i] = Seg(RespOf(FitLevels[i]));
        }

        // arrayA init: min-phase of the smoothed two-level ratio, unity at ~1 kHz.
        var xQ = ScaledSeg(di48, quiet);
        var xL = ScaledSeg(di48, LoudLevel);
        var hQ = WelchPairs([(xQ, Seg(RespOf(quiet)))], 1e-5);
        var hL = WelchPairs([(xL, Seg(RespOf(LoudLevel)))], 1e-5);

        var ratio = new double[Bins];

        for (var i = 0; i < Bins; i++)
        {
            var nq = Math.Sqrt(hQ.Re[i] * hQ.Re[i] + hQ.Im[i] * hQ.Im[i]);
            var nl = Math.Sqrt(hL.Re[i] * hL.Re[i] + hL.Im[i] * hL.Im[i]);

            ratio[i] = nq / Math.Max(nl, 1e-9);
        }

        ratio = Logsmooth(ratio);

        const int bin1k = 1000 * Nfft / SrcRate;
        var norm = Math.Max(ratio[bin1k], 1e-9);

        for (var i = 0; i < Bins; i++)
        {
            ratio[i] /= norm;
        }

        var a48 = MinphaseMag(ratio, ArrayALen);

        // arrayB init: the hard-driven transfer.
        var b48 = Firify(hL, ArrayBLen);

        // Coordinate descent: NL from deconvolved targets, B from multi-level Welch.
        var vs = new double[xsFit.Length][];

        Parallel.For(0, xsFit.Length, i => vs[i] = ConvFir(xsFit[i], a48));

        var gains = new NlGains(1.0, 1.0, 1.0, 1.0);

        for (var round = 0; round < 3; round++)
        {
            var ts = new double[ysFit.Length][];
            var bCur = b48;

            Parallel.For(0, ysFit.Length, i => ts[i] = Deconv(ysFit[i], bCur, 3e-3));

            gains = FitNl(vs, ts);

            var pairs = new (double[], double[])[vs.Length];

            for (var i = 0; i < vs.Length; i++)
            {
                pairs[i] = (NlFwd(vs[i], gains), ysFit[i]);
            }

            b48 = Firify(WelchPairs(pairs, 1e-5), ArrayBLen);
        }

        return (a48, b48, gains);
    }
}
