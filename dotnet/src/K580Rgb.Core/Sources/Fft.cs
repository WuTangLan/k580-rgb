namespace K580Rgb.Core.Sources;

/// <summary>Radix-2 FFT for real input; magnitudes like numpy's abs(rfft(x)).</summary>
public sealed class Fft
{
    readonly int _n;
    readonly double[] _cos, _sin;
    readonly int[] _rev;
    readonly double[] _re, _im;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two");
        _n = n;
        _cos = new double[n / 2];
        _sin = new double[n / 2];
        for (int i = 0; i < n / 2; i++)
        {
            _cos[i] = Math.Cos(-2 * Math.PI * i / n);
            _sin[i] = Math.Sin(-2 * Math.PI * i / n);
        }
        _rev = new int[n];
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _rev[i] = r;
        }
        _re = new double[n];
        _im = new double[n];
    }

    /// <summary>numpy.hanning(n): 0.5 - 0.5 cos(2 pi k / (n - 1)).</summary>
    public static double[] Hanning(int n) =>
        [.. Enumerable.Range(0, n).Select(k => 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / (n - 1)))];

    /// <summary>|rfft(x * window)| into <paramref name="mag"/> (n / 2 + 1 bins).</summary>
    public void Magnitudes(ReadOnlySpan<float> x, double[] window, double[] mag)
    {
        for (int i = 0; i < _n; i++)
        {
            _re[_rev[i]] = x[i] * window[i];
            _im[_rev[i]] = 0;
        }
        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size >> 1, step = _n / size;
            for (int start = 0; start < _n; start += size)
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = _re[b] * wr - _im[b] * wi, ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
        }
        for (int k = 0; k <= _n / 2; k++)
            mag[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
    }

    /// <summary>numpy.geomspace(lo, hi, count).</summary>
    public static double[] GeomSpace(double lo, double hi, int count) =>
        [.. Enumerable.Range(0, count).Select(i => lo * Math.Pow(hi / lo, (double)i / (count - 1)))];
}
