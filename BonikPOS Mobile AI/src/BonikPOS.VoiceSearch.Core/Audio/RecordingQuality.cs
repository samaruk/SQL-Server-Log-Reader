namespace BonikPOS.VoiceSearch.Core.Audio;

public enum RecordingIssue
{
    NoSpeech,
    TooShort,
    TooQuiet,
    TooLoud,
    Clipping,
    Noisy,
}

public sealed record RecordingQualityThresholds
{
    public double MinSpeechSeconds { get; init; } = 1.5;

    public double MinSpeechDbfs { get; init; } = -40;

    public double MaxSpeechDbfs { get; init; } = -6;

    public double MaxClippingRatio { get; init; } = 0.002;

    public double MinSnrDb { get; init; } = 15;
}

/// <summary>Measured properties of a recording. Values are computed on 20 ms frames.</summary>
public sealed record RecordingQualityReport(
    double DurationSeconds,
    double SpeechSeconds,
    double SpeechDbfs,
    double NoiseFloorDbfs,
    double EstimatedSnrDb,
    double ClippingRatio,
    double PeakDbfs,
    IReadOnlyList<RecordingIssue> Issues)
{
    public bool IsAcceptable => Issues.Count == 0;
}

/// <summary>Quality feedback for the microphone test and every enrollment sample.</summary>
public static class RecordingQualityAnalyzer
{
    /// <param name="audio">16 kHz mono audio.</param>
    /// <param name="speechFlags">Optional per-frame (20 ms) speech decisions from the VAD. When null, an energy split is used.</param>
    public static RecordingQualityReport Analyze(
        ReadOnlySpan<float> audio, int sampleRate, IReadOnlyList<bool>? speechFlags, RecordingQualityThresholds? thresholds = null)
    {
        thresholds ??= new RecordingQualityThresholds();
        int frame = sampleRate / 50;
        int frames = audio.Length / frame;
        var levels = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            levels[i] = AudioLevel.RmsDbfs(audio.Slice(i * frame, frame));
        }

        double duration = (double)audio.Length / sampleRate;
        double clipping = AudioLevel.ClippingRatio(audio);
        double peak = AudioLevel.ToDbfs(AudioLevel.Peak(audio));
        var speech = new List<double>();
        var noise = new List<double>();
        if (speechFlags is not null)
        {
            for (int i = 0; i < frames; i++)
            {
                bool isSpeech = i < speechFlags.Count && speechFlags[i];
                (isSpeech ? speech : noise).Add(levels[i]);
            }
        }
        else if (frames > 0)
        {
            var sorted = levels.Order().ToArray();
            double floor = Percentile(sorted, 0.15);
            foreach (double l in levels)
            {
                (l > floor + 12 ? speech : noise).Add(l);
            }
        }

        double speechSeconds = speech.Count * 0.02;
        double speechDb = speech.Count > 0 ? PowerMeanDb(speech) : AudioLevel.SilenceDbfs;
        double noiseDb = noise.Count > 0 ? Percentile(noise.Order().ToArray(), 0.5) : AudioLevel.SilenceDbfs;
        double snr = speech.Count > 0 ? speechDb - noiseDb : 0;

        var issues = new List<RecordingIssue>();
        if (speech.Count == 0)
        {
            issues.Add(RecordingIssue.NoSpeech);
        }
        else
        {
            if (speechSeconds < thresholds.MinSpeechSeconds)
            {
                issues.Add(RecordingIssue.TooShort);
            }

            if (speechDb < thresholds.MinSpeechDbfs)
            {
                issues.Add(RecordingIssue.TooQuiet);
            }

            if (speechDb > thresholds.MaxSpeechDbfs)
            {
                issues.Add(RecordingIssue.TooLoud);
            }

            if (noise.Count > 0 && snr < thresholds.MinSnrDb)
            {
                issues.Add(RecordingIssue.Noisy);
            }
        }

        if (clipping > thresholds.MaxClippingRatio)
        {
            issues.Add(RecordingIssue.Clipping);
        }

        return new RecordingQualityReport(duration, speechSeconds, speechDb, noiseDb, snr, clipping, peak, issues);
    }

    private static double PowerMeanDb(List<double> levelsDb)
    {
        double sum = 0;
        foreach (double l in levelsDb)
        {
            sum += Math.Pow(10, l / 10);
        }

        return 10 * Math.Log10(sum / levelsDb.Count);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return AudioLevel.SilenceDbfs;
        }

        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
