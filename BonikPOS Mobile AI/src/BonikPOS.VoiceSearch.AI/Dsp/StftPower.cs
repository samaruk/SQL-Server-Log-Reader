namespace BonikPOS.VoiceSearch.AI.Dsp;

public enum CenterPadMode
{
    /// <summary>Zero padding (librosa >= 0.10 default).</summary>
    Constant,
    /// <summary>Reflection without repeating the edge sample (torch.stft / numpy "reflect").</summary>
    Reflect,
}

/// <summary>
/// Centered short-time power spectrum with a periodic Hann window (torch.hann_window / scipy "hann" fftbins=True),
/// frame count = 1 + len / hop, as torch.stft(center=True) and librosa.stft(center=True) produce.
/// </summary>
public sealed class StftPower
{
    private readonly RealPowerSpectrum _spectrum;
    private readonly double[] _window;
    private readonly double[] _frameA;
    private readonly double[] _frameB;

    public StftPower(int fftSize, int hopLength, CenterPadMode padMode)
    {
        FftSize = fftSize;
        HopLength = hopLength;
        PadMode = padMode;
        _spectrum = new RealPowerSpectrum(fftSize);
        _window = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / fftSize);
        }

        _frameA = new double[fftSize];
        _frameB = new double[fftSize];
    }

    public int FftSize { get; }

    public int HopLength { get; }

    public CenterPadMode PadMode { get; }

    public int Bins => FftSize / 2 + 1;

    public int FrameCount(int samples) => 1 + samples / HopLength;

    /// <summary>
    /// Computes power spectra of frames [0, frames) into <paramref name="power"/> laid out as [frames, Bins].
    /// Frames whose windowed samples are all zero are filled with zeros without an FFT (padding regions).
    /// </summary>
    public void Compute(ReadOnlySpan<float> audio, int frames, Span<double> power)
    {
        int bins = Bins;
        if (power.Length < frames * bins)
        {
            throw new ArgumentException("Power buffer too small.", nameof(power));
        }

        int t = 0;
        while (t < frames)
        {
            bool aZero = !Fill(audio, t, _frameA);
            bool hasB = t + 1 < frames;
            bool bZero = !hasB || !Fill(audio, t + 1, _frameB);
            var outA = power.Slice(t * bins, bins);
            var outB = hasB ? power.Slice((t + 1) * bins, bins) : Span<double>.Empty;
            if (aZero && bZero)
            {
                outA.Clear();
                outB.Clear();
            }
            else
            {
                _spectrum.Compute(_frameA, hasB ? _frameB : ReadOnlySpan<double>.Empty, outA, outB);
            }

            t += hasB ? 2 : 1;
        }
    }

    /// <returns>false when the windowed frame is all zeros.</returns>
    private bool Fill(ReadOnlySpan<float> audio, int frame, double[] target)
    {
        int pad = FftSize / 2;
        int start = frame * HopLength - pad;
        bool any = false;
        for (int i = 0; i < FftSize; i++)
        {
            int idx = start + i;
            float s;
            if ((uint)idx < (uint)audio.Length)
            {
                s = audio[idx];
            }
            else if (PadMode == CenterPadMode.Reflect && audio.Length > 1)
            {
                s = audio[Reflect(idx, audio.Length)];
            }
            else
            {
                s = 0;
            }

            double v = s * _window[i];
            target[i] = v;
            any |= v != 0;
        }

        return any;
    }

    private static int Reflect(int idx, int length)
    {
        int period = 2 * (length - 1);
        idx = ((idx % period) + period) % period;
        return idx < length ? idx : period - idx;
    }
}
