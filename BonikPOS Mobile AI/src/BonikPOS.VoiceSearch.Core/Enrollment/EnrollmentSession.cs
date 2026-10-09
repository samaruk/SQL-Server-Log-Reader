using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Core.Enrollment;

public sealed record EnrollmentOptions
{
    public int RequiredSamples { get; init; } = 5;

    public int MaxSamples { get; init; } = 8;

    public double MaxSampleSeconds { get; init; } = 9;

    /// <summary>Recording stops automatically after this much silence following speech.</summary>
    public double EndSilenceSeconds { get; init; } = 1.2;

    /// <summary>Recording stops if no speech starts within this time.</summary>
    public double StartTimeoutSeconds { get; init; } = 7;

    public RecordingQualityThresholds Quality { get; init; } = new();
}

/// <summary>Live level reading for the recording indicator.</summary>
public readonly record struct LevelReading(double RmsDbfs, double PeakDbfs, bool SpeechDetected, double ElapsedSeconds);

public sealed record EnrollmentSampleResult(
    int Number,
    EnrollmentPrompt Prompt,
    RecordingQualityReport Quality,
    bool Accepted,
    double? ConsistencyWithOtherSamples,
    double? MinWindowSimilarity,
    IReadOnlyList<EnrollmentIssue> Issues);

public enum EnrollmentIssue
{
    NoSpeech,
    TooShort,
    TooQuiet,
    TooLoud,
    Clipping,
    Noisy,
    /// <summary>Parts of the sample do not sound like the rest - possibly another person spoke.</summary>
    PossibleSecondSpeaker,
    /// <summary>The sample does not sound like the owner's other samples.</summary>
    InconsistentWithOtherSamples,
    SpeakerModelUnavailable,
}

public sealed record EnrollmentProgress(int Accepted, int Required, int Rejected, bool CanComplete);

/// <summary>
/// Runs one enrollment: records samples, checks their quality, extracts embeddings and builds the encrypted-at-rest
/// <see cref="VoiceProfile"/>. Audio lives only in memory and is cleared when the session ends; it is never written
/// to storage.
/// </summary>
public sealed class EnrollmentSession : IDisposable
{
    private readonly IMicrophoneCapture _capture;
    private readonly IVoiceModelRuntime _runtime;
    private readonly EnrollmentOptions _options;
    private readonly List<(EnrollmentPrompt Prompt, SpeakerEmbedding Embedding, double SpeechSeconds)> _accepted = [];
    private float[]? _lastAudio;
    private int _rejected;
    private int _number;

    public EnrollmentSession(IMicrophoneCapture capture, IVoiceModelRuntime runtime, EnrollmentOptions? options = null)
    {
        _capture = capture;
        _runtime = runtime;
        _options = options ?? new EnrollmentOptions();
    }

    public EnrollmentProgress Progress => new(_accepted.Count, _options.RequiredSamples, _rejected, _accepted.Count >= _options.RequiredSamples);

    public bool CanRecordMore => _accepted.Count < _options.MaxSamples;

    /// <summary>The most recent recording, kept in memory only so the owner can listen to it before continuing.</summary>
    public ReadOnlyMemory<float> LastRecording => _lastAudio;

    /// <summary>
    /// Records one sample. <paramref name="stopRecording"/> ends the recording early but still evaluates it;
    /// <paramref name="cancellationToken"/> abandons it.
    /// </summary>
    public async Task<EnrollmentSampleResult> RecordSampleAsync(
        EnrollmentPrompt prompt, IProgress<LevelReading>? level, CancellationToken stopRecording, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var encoder = _runtime.SpeakerEncoder;
        var (audio, flags, speech) = await CaptureAsync(level, stopRecording, cancellationToken).ConfigureAwait(false);
        ClearLast();
        _lastAudio = audio;
        _number++;

        var quality = RecordingQualityAnalyzer.Analyze(audio, 16000, flags, _options.Quality);
        var issues = quality.Issues.Select(Map).ToList();
        double? consistency = null;
        double? minWindow = null;
        SpeakerEmbedding? embedding = null;
        if (encoder is null)
        {
            issues.Add(EnrollmentIssue.SpeakerModelUnavailable);
        }
        else if (!issues.Contains(EnrollmentIssue.NoSpeech) && speech.Length >= encoder.Info.MinimumSpeechSeconds * 16000)
        {
            var calibration = encoder.Info.Calibration;
            embedding = await Task.Run(() => encoder.EmbedUtterance(speech, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (speech.Length >= encoder.Info.WindowSeconds * 1.5 * 16000)
            {
                var windows = await Task.Run(
                    () => encoder.EmbedWindows(speech, encoder.Info.WindowSeconds, encoder.Info.WindowSeconds / 2, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                minWindow = windows.Min(w => w.Embedding.CosineSimilarity(embedding));
                if (minWindow < calibration.SingleSpeakerMinWindow)
                {
                    issues.Add(EnrollmentIssue.PossibleSecondSpeaker);
                }
            }

            if (_accepted.Count > 0)
            {
                var others = SpeakerEmbedding.Centroid(_accepted.Select(a => a.Embedding).ToList());
                consistency = embedding.CosineSimilarity(others);
                if (consistency < calibration.EnrollmentMinConsistency)
                {
                    issues.Add(EnrollmentIssue.InconsistentWithOtherSamples);
                }
            }
        }

        bool accepted = issues.Count == 0 && embedding is not null;
        if (accepted)
        {
            _accepted.Add((prompt, embedding!, speech.Length / 16000.0));
        }
        else
        {
            _rejected++;
        }

        Array.Clear(speech);
        return new EnrollmentSampleResult(_number, prompt, quality, accepted, consistency, minWindow, issues);
    }

    /// <summary>Builds the profile from the accepted samples. Requires <see cref="EnrollmentOptions.RequiredSamples"/> samples.</summary>
    public VoiceProfile Complete(ConsentRecord consent, string appVersion, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var encoder = _runtime.SpeakerEncoder ?? throw new InvalidOperationException("The speaker-encoder model is not available.");
        if (_accepted.Count < _options.RequiredSamples)
        {
            throw new InvalidOperationException($"{_options.RequiredSamples} accepted samples are required; {_accepted.Count} recorded.");
        }

        var embeddings = _accepted.Select(a => a.Embedding).ToList();
        var calibration = Calibrate(embeddings, encoder.Info.Calibration);
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var centroid = SpeakerEmbedding.Centroid(embeddings);
        var profile = new VoiceProfile
        {
            CreatedUtc = now,
            UpdatedUtc = now,
            Consent = consent,
            Embeddings =
            [
                new EnrolledEmbedding(
                    encoder.Info.ModelId, encoder.Info.ModelVersion, encoder.Info.ModelSha256, centroid.Vector,
                    embeddings.Count, calibration.Mean, calibration.Min, calibration.SuggestedThreshold),
            ],
            Enrollment = new EnrollmentSummary(
                _accepted.Count, _rejected, _accepted.Sum(a => a.SpeechSeconds),
                [.. _accepted.Select(a => a.Prompt.Language).Distinct()], appVersion),
        };
        ClearLast();
        return profile;
    }

    /// <summary>
    /// Leave-one-out calibration: each sample is compared with the centroid of the others. The suggested accept
    /// threshold sits below the owner's own score distribution, bounded by the encoder's limits.
    /// </summary>
    public static (double Mean, double Min, double StdDev, double SuggestedThreshold) Calibrate(
        IReadOnlyList<SpeakerEmbedding> samples, SpeakerCalibration limits)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(limits);
        if (samples.Count < 2)
        {
            return (double.NaN, double.NaN, double.NaN, limits.DefaultAcceptThreshold);
        }

        var scores = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var others = SpeakerEmbedding.Centroid(samples.Where((_, j) => j != i).ToList());
            scores[i] = samples[i].CosineSimilarity(others);
        }

        double mean = scores.Average();
        double std = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Sum() / scores.Length);
        double suggested = Math.Min(mean - 2 * std, scores.Min() - 0.03);
        suggested = Math.Clamp(suggested, limits.MinAcceptThreshold, limits.MaxAcceptThreshold);
        return (mean, scores.Min(), std, suggested);
    }

    public void Dispose() => ClearLast();

    private void ClearLast()
    {
        if (_lastAudio is not null)
        {
            Array.Clear(_lastAudio);
            _lastAudio = null;
        }
    }

    private static EnrollmentIssue Map(RecordingIssue issue) => issue switch
    {
        RecordingIssue.NoSpeech => EnrollmentIssue.NoSpeech,
        RecordingIssue.TooShort => EnrollmentIssue.TooShort,
        RecordingIssue.TooQuiet => EnrollmentIssue.TooQuiet,
        RecordingIssue.TooLoud => EnrollmentIssue.TooLoud,
        RecordingIssue.Clipping => EnrollmentIssue.Clipping,
        _ => EnrollmentIssue.Noisy,
    };

    private async Task<(float[] Audio, List<bool> Flags, float[] Speech)> CaptureAsync(
        IProgress<LevelReading>? level, CancellationToken stopRecording, CancellationToken cancellationToken)
    {
        int maxSamples = (int)(_options.MaxSampleSeconds * 16000);
        var audio = new float[maxSamples];
        var speech = new float[maxSamples];
        int length = 0, speechLength = 0;
        var frameFlags = new List<bool>();
        using var vad = _runtime.CreateVoiceActivityDetector();
        var chunk = new float[vad.ChunkSamples];
        int chunkFill = 0;
        float lastProbability = 0;
        bool heardSpeech = false;
        int silentSamples = 0;

        await _capture.StartAsync(new MicrophoneCaptureOptions { FrameMilliseconds = 20, QueueCapacityFrames = 250 }, cancellationToken)
            .ConfigureAwait(false);
        var frame = new float[_capture.Frames.FrameSamples];
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopRecording, cancellationToken);
            while (length + frame.Length <= maxSamples)
            {
                AudioFrameReadResult read;
                try
                {
                    read = await _capture.Frames.ReadFrameAsync(frame, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break; // owner pressed "Stop": keep what was recorded
                }

                if (!read.HasFrame)
                {
                    break;
                }

                frame.CopyTo(audio, length);
                length += frame.Length;
                for (int i = 0; i < frame.Length; i++)
                {
                    chunk[chunkFill++] = frame[i];
                    if (chunkFill == chunk.Length)
                    {
                        chunkFill = 0;
                        lastProbability = vad.Process(chunk);
                        if (lastProbability >= 0.35f)
                        {
                            chunk.CopyTo(speech, speechLength);
                            speechLength += chunk.Length;
                        }
                    }
                }

                bool isSpeech = lastProbability >= 0.5f;
                frameFlags.Add(isSpeech);
                heardSpeech |= isSpeech;
                silentSamples = isSpeech ? 0 : silentSamples + frame.Length;
                double elapsed = length / 16000.0;
                level?.Report(new LevelReading(AudioLevel.RmsDbfs(frame), AudioLevel.ToDbfs(AudioLevel.Peak(frame)), isSpeech, elapsed));
                if (heardSpeech && silentSamples >= _options.EndSilenceSeconds * 16000)
                {
                    break;
                }

                if (!heardSpeech && elapsed >= _options.StartTimeoutSeconds)
                {
                    break;
                }
            }
        }
        finally
        {
            await _capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = audio.AsSpan(0, length).ToArray();
        var speechOnly = speech.AsSpan(0, speechLength).ToArray();
        Array.Clear(audio);
        Array.Clear(speech);
        return (trimmed, frameFlags, speechOnly);
    }
}
