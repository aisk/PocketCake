namespace PocketCake.Core.Dsp;

/// <summary>Self-contained radix-2 complex FFT (all `.nam` FFT sizes are a power of two).</summary>
internal sealed class Fft
{
    private readonly int[] _rev;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int n)
    {
        N = n;
        _rev = new int[n];
        _cos = new double[n / 2];
        _sin = new double[n / 2];

        var bits = 0;

        while ((1 << bits) < n)
        {
            bits++;
        }

        for (var i = 0; i < n; i++)
        {
            var r = 0;

            for (var b = 0; b < bits; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    r |= 1 << (bits - 1 - b);
                }
            }

            _rev[i] = r;
        }

        for (var i = 0; i < n / 2; i++)
        {
            var angle = 2.0 * Math.PI * i / n;

            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
        }
    }

    public int N { get; }

    /// <summary>
    /// In-place complex FFT. Forward uses e^{-i.}, inverse e^{+i.}; both are
    /// UNNORMALIZED (matches rustfft/realfft — callers divide by N by hand).
    /// Thread-safe: the twiddle tables are read-only.
    /// </summary>
    public void Transform(double[] re, double[] im, bool inverse)
    {
        var n = N;

        for (var i = 0; i < n; i++)
        {
            var j = _rev[i];

            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len >> 1;
            var step = n / len;

            for (var i = 0; i < n; i += len)
            {
                for (var j = 0; j < half; j++)
                {
                    var m = j * step;
                    var wr = _cos[m];
                    var wi = inverse ? _sin[m] : -_sin[m];
                    var a = i + j;
                    var b = a + half;
                    var tr = wr * re[b] - wi * im[b];
                    var ti = wr * im[b] + wi * re[b];

                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
