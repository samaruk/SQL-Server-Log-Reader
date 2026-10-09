namespace BonikPOS.VoiceSearch.AI.Dsp;

/// <summary>
/// Mixed-radix complex FFT (decimation in time) for any size whose prime factors are small. Whisper and librosa use
/// n_fft = 400 = 4*4*5*5, which a power-of-two FFT cannot compute exactly. Instances own scratch buffers and are
/// not thread-safe; create one per feature extractor.
/// </summary>
public sealed class Fft
{
    private readonly int[] _factors;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly double[] _tRe = new double[16];
    private readonly double[] _tIm = new double[16];
    private readonly double[] _yRe = new double[16];
    private readonly double[] _yIm = new double[16];

    public Fft(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        Size = size;
        _factors = Factorize(size);
        _cos = new double[size];
        _sin = new double[size];
        for (int i = 0; i < size; i++)
        {
            double a = -2.0 * Math.PI * i / size;
            _cos[i] = Math.Cos(a);
            _sin[i] = Math.Sin(a);
        }
    }

    public int Size { get; }

    /// <summary>Forward transform: out[k] = sum_n in[n] * exp(-2*pi*i*k*n/N).</summary>
    public void Forward(ReadOnlySpan<double> inRe, ReadOnlySpan<double> inIm, Span<double> outRe, Span<double> outIm)
    {
        if (inRe.Length != Size || inIm.Length != Size || outRe.Length != Size || outIm.Length != Size)
        {
            throw new ArgumentException($"All buffers must have length {Size}.");
        }

        Recurse(inRe, inIm, 0, 1, Size, outRe, outIm, 0);
    }

    private void Recurse(
        ReadOnlySpan<double> inRe, ReadOnlySpan<double> inIm, int offset, int stride, int n,
        Span<double> outRe, Span<double> outIm, int factorIndex)
    {
        if (n == 1)
        {
            outRe[0] = inRe[offset];
            outIm[0] = inIm[offset];
            return;
        }

        int p = _factors[factorIndex];
        int m = n / p;
        for (int q = 0; q < p; q++)
        {
            Recurse(inRe, inIm, offset + q * stride, stride * p, m, outRe.Slice(q * m, m), outIm.Slice(q * m, m), factorIndex + 1);
        }

        int twStep = Size / n;     // W_n^j = W_Size^(j*twStep)
        int rootStep = Size / p;   // W_p^j = W_Size^(j*rootStep)
        for (int k = 0; k < m; k++)
        {
            for (int q = 0; q < p; q++)
            {
                double re = outRe[q * m + k];
                double im = outIm[q * m + k];
                if (q != 0 && k != 0)
                {
                    int idx = q * k * twStep % Size;
                    double c = _cos[idx], s = _sin[idx];
                    (re, im) = (re * c - im * s, re * s + im * c);
                }

                _tRe[q] = re;
                _tIm[q] = im;
            }

            for (int r = 0; r < p; r++)
            {
                double sumRe = 0, sumIm = 0;
                for (int q = 0; q < p; q++)
                {
                    int idx = q * r % p * rootStep;
                    double c = _cos[idx], s = _sin[idx];
                    sumRe += _tRe[q] * c - _tIm[q] * s;
                    sumIm += _tRe[q] * s + _tIm[q] * c;
                }

                _yRe[r] = sumRe;
                _yIm[r] = sumIm;
            }

            for (int r = 0; r < p; r++)
            {
                outRe[r * m + k] = _yRe[r];
                outIm[r * m + k] = _yIm[r];
            }
        }
    }

    private static int[] Factorize(int n)
    {
        var factors = new List<int>();
        foreach (int f in new[] { 4, 2, 3, 5, 7, 11, 13 })
        {
            while (n % f == 0)
            {
                factors.Add(f);
                n /= f;
            }
        }

        if (n != 1)
        {
            throw new ArgumentException($"FFT size has a prime factor larger than 13 ({n}).");
        }

        return [.. factors];
    }
}

/// <summary>Power spectrum of real frames, two frames per complex FFT (real/imaginary packing).</summary>
public sealed class RealPowerSpectrum
{
    private readonly Fft _fft;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _outRe;
    private readonly double[] _outIm;

    public RealPowerSpectrum(int fftSize)
    {
        _fft = new Fft(fftSize);
        _re = new double[fftSize];
        _im = new double[fftSize];
        _outRe = new double[fftSize];
        _outIm = new double[fftSize];
    }

    public int FftSize => _fft.Size;

    public int Bins => _fft.Size / 2 + 1;

    /// <summary>|X[k]|^2 for k = 0..N/2 of two real frames at once. <paramref name="frameB"/> may be empty.</summary>
    public void Compute(ReadOnlySpan<double> frameA, ReadOnlySpan<double> frameB, Span<double> powerA, Span<double> powerB)
    {
        int n = _fft.Size;
        frameA.CopyTo(_re);
        if (frameB.IsEmpty)
        {
            Array.Clear(_im);
        }
        else
        {
            frameB.CopyTo(_im);
        }

        _fft.Forward(_re, _im, _outRe, _outIm);
        for (int k = 0; k <= n / 2; k++)
        {
            int nk = (n - k) % n;
            double zr = _outRe[k], zi = _outIm[k];
            double cr = _outRe[nk], ci = -_outIm[nk]; // conj(Z[N-k])
            double ar = 0.5 * (zr + cr), ai = 0.5 * (zi + ci);
            powerA[k] = ar * ar + ai * ai;
            if (!powerB.IsEmpty)
            {
                // B = (Z - conj(Z[N-k])) / 2i
                double br = 0.5 * (zi - ci), bi = -0.5 * (zr - cr);
                powerB[k] = br * br + bi * bi;
            }
        }
    }
}
