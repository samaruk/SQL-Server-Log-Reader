namespace BonikPOS.VoiceSearch.AI.Dsp;

/// <summary>
/// Whisper's log-mel front-end (whisper/audio.py log_mel_spectrogram): torch.stft(n_fft=400, hop=160, Hann,
/// center=True, reflect), last frame dropped, Slaney mel bank, log10(max(x, 1e-10)), floor at max - 8, (x + 4) / 4.
/// </summary>
public sealed class WhisperLogMel
{
    public const int SampleRate = 16000;
    public const int FftSize = 400;
    public const int HopLength = 160;

    private readonly StftPower _stft = new(FftSize, HopLength, CenterPadMode.Reflect);
    private readonly MelFilterBank _mel;
    private double[] _power = [];
    private readonly double[] _melRow;

    public WhisperLogMel(int mels)
    {
        if (mels is not (80 or 128))
        {
            throw new ArgumentOutOfRangeException(nameof(mels), "Whisper uses 80 or 128 mel bins.");
        }

        Mels = mels;
        _mel = MelFilterBank.Slaney(SampleRate, FftSize, mels);
        _melRow = new double[mels];
    }

    public int Mels { get; }

    /// <summary>
    /// Pads/trims <paramref name="audio"/> to <paramref name="frames"/> * 160 samples and writes the normalised
    /// log-mel spectrogram as [mels, frames] (row-major) into <paramref name="output"/>.
    /// </summary>
    public void Compute(ReadOnlySpan<float> audio, int frames, Span<float> output)
    {
        if (output.Length < Mels * frames)
        {
            throw new ArgumentException("Output buffer too small.", nameof(output));
        }

        int samples = frames * HopLength;
        float[]? padded = null;
        if (audio.Length != samples)
        {
            padded = new float[samples];
            audio[..Math.Min(audio.Length, samples)].CopyTo(padded);
            audio = padded;
        }

        // torch.stft yields 1 + samples/hop frames; Whisper drops the last one.
        int bins = _stft.Bins;
        if (_power.Length < frames * bins)
        {
            _power = new double[frames * bins];
        }

        _stft.Compute(audio, frames, _power);
        double max = double.MinValue;
        for (int t = 0; t < frames; t++)
        {
            _mel.Apply(_power.AsSpan(t * bins, bins), _melRow);
            for (int m = 0; m < Mels; m++)
            {
                double v = Math.Log10(Math.Max(_melRow[m], 1e-10));
                output[m * frames + t] = (float)v;
                if (v > max)
                {
                    max = v;
                }
            }
        }

        double floor = max - 8.0;
        for (int i = 0; i < Mels * frames; i++)
        {
            double v = Math.Max(output[i], floor);
            output[i] = (float)((v + 4.0) / 4.0);
        }
    }
}
