namespace BonikPOS.VoiceSearch.AI.Dsp;

/// <summary>
/// librosa.feature.melspectrogram(y, sr, n_fft, hop_length, n_mels) with power=2, center=True and a Slaney mel bank.
/// Output is NOT logarithmic - this is the input the Resemblyzer GE2E encoder was trained on.
/// </summary>
public sealed class PowerMelSpectrogram
{
    private readonly StftPower _stft;
    private readonly MelFilterBank _mel;
    private double[] _power = [];
    private readonly double[] _row;

    public PowerMelSpectrogram(int sampleRate, int fftSize, int hopLength, int mels, CenterPadMode padMode = CenterPadMode.Constant)
    {
        _stft = new StftPower(fftSize, hopLength, padMode);
        _mel = MelFilterBank.Slaney(sampleRate, fftSize, mels);
        _row = new double[mels];
        Mels = mels;
    }

    public int Mels { get; }

    public int FrameCount(int samples) => _stft.FrameCount(samples);

    /// <summary>Writes [frames, mels] (row-major) for all 1 + len/hop frames.</summary>
    public float[] Compute(ReadOnlySpan<float> audio)
    {
        int frames = _stft.FrameCount(audio.Length);
        int bins = _stft.Bins;
        if (_power.Length < frames * bins)
        {
            _power = new double[frames * bins];
        }

        _stft.Compute(audio, frames, _power);
        var result = new float[frames * Mels];
        for (int t = 0; t < frames; t++)
        {
            _mel.Apply(_power.AsSpan(t * bins, bins), _row);
            for (int m = 0; m < Mels; m++)
            {
                result[t * Mels + m] = (float)_row[m];
            }
        }

        return result;
    }
}
