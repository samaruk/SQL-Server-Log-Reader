namespace BonikPOS.VoiceSearch.AI.Dsp;

public enum KaldiWindowType
{
    Povey,
    Hamming,
    Hanning,
}

public sealed record KaldiFbankOptions
{
    public int SampleRate { get; init; } = 16000;

    public double FrameLengthMs { get; init; } = 25;

    public double FrameShiftMs { get; init; } = 10;

    public double PreemphCoeff { get; init; } = 0.97;

    public bool RemoveDcOffset { get; init; } = true;

    public KaldiWindowType Window { get; init; } = KaldiWindowType.Povey;

    public int NumBins { get; init; } = 80;

    public double LowFreq { get; init; } = 20;

    public double HighFreq { get; init; }

    /// <summary>Multiplier applied to [-1, 1] samples first (WeSpeaker-style models expect 32768).</summary>
    public float InputScale { get; init; } = 32768f;
}

/// <summary>
/// Kaldi-compatible log mel filter-bank features (snip_edges=true, dither=0, power spectrum, natural log with
/// FLT_EPSILON floor), as computed by kaldi-native-fbank / torchaudio.compliance.kaldi.fbank. Front-end of
/// WeSpeaker and 3D-Speaker style speaker encoders.
/// </summary>
public sealed class KaldiFbank
{
    // std::numeric_limits<float>::epsilon(), the floor Kaldi applies before the log.
    private const double FltEpsilon = 1.1920928955078125e-07;

    private readonly KaldiFbankOptions _options;
    private readonly int _frameLength;
    private readonly int _frameShift;
    private readonly RealPowerSpectrum _spectrum;
    private readonly MelFilterBank _mel;
    private readonly double[] _window;
    private readonly double[] _frame;
    private readonly double[] _power;
    private readonly double[] _row;

    public KaldiFbank(KaldiFbankOptions options)
    {
        _options = options;
        _frameLength = (int)(options.SampleRate * options.FrameLengthMs / 1000);
        _frameShift = (int)(options.SampleRate * options.FrameShiftMs / 1000);
        int fft = 1;
        while (fft < _frameLength)
        {
            fft <<= 1;
        }

        _spectrum = new RealPowerSpectrum(fft);
        _mel = MelFilterBank.Kaldi(options.SampleRate, fft, options.NumBins, options.LowFreq, options.HighFreq);
        _window = new double[_frameLength];
        for (int i = 0; i < _frameLength; i++)
        {
            double a = 2 * Math.PI * i / (_frameLength - 1);
            _window[i] = options.Window switch
            {
                KaldiWindowType.Povey => Math.Pow(0.5 - 0.5 * Math.Cos(a), 0.85),
                KaldiWindowType.Hamming => 0.54 - 0.46 * Math.Cos(a),
                _ => 0.5 - 0.5 * Math.Cos(a),
            };
        }

        _frame = new double[fft];
        _power = new double[fft / 2 + 1];
        _row = new double[options.NumBins];
    }

    public int NumBins => _options.NumBins;

    public int FrameCount(int samples) => samples < _frameLength ? 0 : 1 + (samples - _frameLength) / _frameShift;

    /// <summary>Returns [frames, NumBins] row-major.</summary>
    public float[] Compute(ReadOnlySpan<float> audio)
    {
        int frames = FrameCount(audio.Length);
        var result = new float[frames * NumBins];
        float scale = _options.InputScale;
        for (int t = 0; t < frames; t++)
        {
            int start = t * _frameShift;
            double mean = 0;
            for (int i = 0; i < _frameLength; i++)
            {
                _frame[i] = audio[start + i] * (double)scale;
                mean += _frame[i];
            }

            if (_options.RemoveDcOffset)
            {
                mean /= _frameLength;
                for (int i = 0; i < _frameLength; i++)
                {
                    _frame[i] -= mean;
                }
            }

            if (_options.PreemphCoeff != 0)
            {
                for (int i = _frameLength - 1; i > 0; i--)
                {
                    _frame[i] -= _options.PreemphCoeff * _frame[i - 1];
                }

                _frame[0] -= _options.PreemphCoeff * _frame[0];
            }

            for (int i = 0; i < _frameLength; i++)
            {
                _frame[i] *= _window[i];
            }

            Array.Clear(_frame, _frameLength, _frame.Length - _frameLength);
            _spectrum.Compute(_frame, ReadOnlySpan<double>.Empty, _power, Span<double>.Empty);
            _mel.Apply(_power, _row);
            for (int m = 0; m < NumBins; m++)
            {
                result[t * NumBins + m] = (float)Math.Log(Math.Max(_row[m], FltEpsilon));
            }
        }

        return result;
    }

    /// <summary>Subtracts the per-utterance mean of every bin (cepstral mean normalisation over time).</summary>
    public static void SubtractMean(Span<float> features, int bins)
    {
        int frames = features.Length / bins;
        if (frames == 0)
        {
            return;
        }

        for (int m = 0; m < bins; m++)
        {
            double sum = 0;
            for (int t = 0; t < frames; t++)
            {
                sum += features[t * bins + m];
            }

            float mean = (float)(sum / frames);
            for (int t = 0; t < frames; t++)
            {
                features[t * bins + m] -= mean;
            }
        }
    }
}
