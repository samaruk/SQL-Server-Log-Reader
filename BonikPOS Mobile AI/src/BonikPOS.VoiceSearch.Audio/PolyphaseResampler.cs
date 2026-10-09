namespace BonikPOS.VoiceSearch.Audio;

/// <summary>
/// Streaming rational sample-rate converter (polyphase FIR, Kaiser-windowed sinc). State is carried across calls so
/// arbitrary block sizes produce a continuous output. Used when the device cannot capture at 16 kHz directly
/// (typical iOS hardware runs at 48 kHz).
/// </summary>
public sealed class PolyphaseResampler
{
    private readonly int _up;
    private readonly int _down;
    private readonly int _tapsPerPhase;
    private readonly float[][] _phases;
    private readonly float[] _history;
    private long _position; // in units of 1/_up input samples, relative to the first sample of the current block

    public PolyphaseResampler(int inputRate, int outputRate, int tapsPerPhase = 32, double rolloff = 0.92, double kaiserBeta = 8.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tapsPerPhase, 4);
        int g = Gcd(inputRate, outputRate);
        _up = outputRate / g;
        _down = inputRate / g;
        InputRate = inputRate;
        OutputRate = outputRate;
        _tapsPerPhase = tapsPerPhase;

        int n = tapsPerPhase * _up;
        double cutoff = 0.5 / Math.Max(_up, _down) * rolloff; // cycles per sample at the up-sampled rate
        double center = (n - 1) / 2.0;
        double i0Beta = BesselI0(kaiserBeta);
        var proto = new double[n];
        for (int j = 0; j < n; j++)
        {
            double x = j - center;
            double sinc = x == 0 ? 1.0 : Math.Sin(2 * Math.PI * cutoff * x) / (2 * Math.PI * cutoff * x);
            double r = 2.0 * j / (n - 1) - 1.0;
            double window = BesselI0(kaiserBeta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
            proto[j] = 2 * cutoff * sinc * window * _up;
        }

        _phases = new float[_up][];
        for (int p = 0; p < _up; p++)
        {
            _phases[p] = new float[tapsPerPhase];
            for (int k = 0; k < tapsPerPhase; k++)
            {
                _phases[p][k] = (float)proto[p + k * _up];
            }
        }

        _history = new float[tapsPerPhase - 1];
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Delay introduced by the filter, in output samples.</summary>
    public double LatencyOutputSamples => (_tapsPerPhase * _up - 1) / 2.0 / _down;

    /// <summary>Upper bound of output samples produced for <paramref name="inputSamples"/> input samples.</summary>
    public int MaxOutputFor(int inputSamples) => (int)((long)inputSamples * _up / _down) + 2;

    /// <summary>Resamples <paramref name="input"/>; returns the number of samples written to <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < MaxOutputFor(input.Length))
        {
            throw new ArgumentException("Output buffer too small; use MaxOutputFor().", nameof(output));
        }

        int produced = 0;
        int hist = _history.Length;
        long limit = (long)input.Length * _up;
        while (_position < limit)
        {
            int i = (int)(_position / _up);
            int phase = (int)(_position % _up);
            float[] h = _phases[phase];
            float acc = 0;
            for (int k = 0; k < h.Length; k++)
            {
                int idx = i - k;
                float x = idx >= 0 ? input[idx] : _history[hist + idx];
                acc += h[k] * x;
            }

            output[produced++] = acc;
            _position += _down;
        }

        _position -= limit;
        // Keep the last (taps - 1) input samples as history for the next block.
        if (input.Length >= hist)
        {
            input[^hist..].CopyTo(_history);
        }
        else
        {
            Array.Copy(_history, input.Length, _history, 0, hist - input.Length);
            input.CopyTo(_history.AsSpan(hist - input.Length));
        }

        return produced;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _position = 0;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 50; k++)
        {
            term *= half / k;
            double t2 = term * term;
            sum += t2;
            if (t2 < 1e-12 * sum)
            {
                break;
            }
        }

        return sum;
    }
}
