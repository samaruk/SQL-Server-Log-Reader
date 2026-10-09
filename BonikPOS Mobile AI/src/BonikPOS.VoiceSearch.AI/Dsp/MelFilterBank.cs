namespace BonikPOS.VoiceSearch.AI.Dsp;

/// <summary>Triangular mel filter banks, stored densely as [mels, bins] in row-major order.</summary>
public sealed class MelFilterBank
{
    private MelFilterBank(float[] weights, int mels, int bins, int[] first, int[] last)
    {
        Weights = weights;
        Mels = mels;
        Bins = bins;
        _first = first;
        _last = last;
    }

    private readonly int[] _first;
    private readonly int[] _last;

    public float[] Weights { get; }

    public int Mels { get; }

    public int Bins { get; }

    /// <summary>
    /// librosa.filters.mel(sr, n_fft, n_mels, fmin=0, fmax=sr/2, htk=False, norm="slaney") - the bank used by
    /// Whisper (whisper/assets/mel_filters.npz) and by librosa.feature.melspectrogram (GE2E encoder).
    /// </summary>
    public static MelFilterBank Slaney(int sampleRate, int fftSize, int mels, double fmin = 0, double? fmax = null)
    {
        double maxHz = fmax ?? sampleRate / 2.0;
        int bins = fftSize / 2 + 1;
        var fftFreqs = new double[bins];
        for (int i = 0; i < bins; i++)
        {
            fftFreqs[i] = (double)i * sampleRate / fftSize;
        }

        double minMel = HzToSlaneyMel(fmin), maxMel = HzToSlaneyMel(maxHz);
        var melF = new double[mels + 2];
        for (int i = 0; i < melF.Length; i++)
        {
            melF[i] = SlaneyMelToHz(minMel + (maxMel - minMel) * i / (mels + 1));
        }

        var w = new float[mels * bins];
        for (int m = 0; m < mels; m++)
        {
            double lowerWidth = melF[m + 1] - melF[m];
            double upperWidth = melF[m + 2] - melF[m + 1];
            double enorm = 2.0 / (melF[m + 2] - melF[m]);
            for (int b = 0; b < bins; b++)
            {
                double lower = (fftFreqs[b] - melF[m]) / lowerWidth;
                double upper = (melF[m + 2] - fftFreqs[b]) / upperWidth;
                double v = Math.Max(0, Math.Min(lower, upper));
                w[m * bins + b] = (float)(v * enorm);
            }
        }

        return Build(w, mels, bins);
    }

    /// <summary>Kaldi mel banks (kaldi-native-fbank / torchaudio.compliance.kaldi): triangles in mel = 1127 ln(1 + f/700).</summary>
    /// <param name="fftSize">Padded window size; Kaldi uses bins 0..fftSize/2-1.</param>
    public static MelFilterBank Kaldi(int sampleRate, int fftSize, int mels, double lowFreq = 20, double highFreq = 0)
    {
        double nyquist = sampleRate / 2.0;
        double high = highFreq <= 0 ? nyquist + highFreq : highFreq;
        int bins = fftSize / 2;
        double binWidth = (double)sampleRate / fftSize;
        double melLow = KaldiMel(lowFreq), melHigh = KaldiMel(high);
        double delta = (melHigh - melLow) / (mels + 1);
        var w = new float[mels * bins];
        for (int m = 0; m < mels; m++)
        {
            double left = melLow + m * delta, center = melLow + (m + 1) * delta, right = melLow + (m + 2) * delta;
            for (int b = 0; b < bins; b++)
            {
                double mel = KaldiMel(binWidth * b);
                if (mel > left && mel < right)
                {
                    double v = mel <= center ? (mel - left) / (center - left) : (right - mel) / (right - center);
                    w[m * bins + b] = (float)v;
                }
            }
        }

        return Build(w, mels, bins);
    }

    /// <summary>output[m] = sum_b W[m,b] * power[b], iterating only over each filter's non-zero support.</summary>
    public void Apply(ReadOnlySpan<double> power, Span<double> output)
    {
        for (int m = 0; m < Mels; m++)
        {
            double sum = 0;
            int row = m * Bins;
            for (int b = _first[m]; b <= _last[m]; b++)
            {
                sum += Weights[row + b] * power[b];
            }

            output[m] = sum;
        }
    }

    public static double HzToSlaneyMel(double hz)
    {
        const double fSp = 200.0 / 3, minLogHz = 1000.0, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27.0;
        return hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) / logStep : hz / fSp;
    }

    public static double SlaneyMelToHz(double mel)
    {
        const double fSp = 200.0 / 3, minLogHz = 1000.0, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27.0;
        return mel >= minLogMel ? minLogHz * Math.Exp(logStep * (mel - minLogMel)) : fSp * mel;
    }

    public static double KaldiMel(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);

    private static MelFilterBank Build(float[] w, int mels, int bins)
    {
        var first = new int[mels];
        var last = new int[mels];
        for (int m = 0; m < mels; m++)
        {
            first[m] = 0;
            last[m] = -1;
            for (int b = 0; b < bins; b++)
            {
                if (w[m * bins + b] != 0)
                {
                    if (last[m] < 0)
                    {
                        first[m] = b;
                    }

                    last[m] = b;
                }
            }
        }

        return new MelFilterBank(w, mels, bins, first, last);
    }
}
