namespace BonikPOS.VoiceSearch.Core.Telemetry;

/// <summary>Metric names. Values are durations in milliseconds unless stated otherwise. No audio or text is ever recorded.</summary>
public static class TelemetryMetrics
{
    public const string CaptureToProcessedMs = "audio.capture_to_processed_ms";

    /// <summary>Platform input latency (microphone converter to app), as estimated by the capture implementation.</summary>
    public const string InputLatencyMs = "audio.input_latency_ms";
    public const string FrameProcessingMs = "pipeline.frame_processing_ms";
    public const string TseChunkMs = "tse.chunk_ms";
    public const string VadChunkMs = "vad.chunk_ms";
    public const string SpeakerVerifyMs = "speaker.verify_ms";
    public const string SpeakerEmbedMs = "speaker.embed_ms";
    public const string AsrFeatureMs = "asr.features_ms";
    public const string AsrEncoderMs = "asr.encoder_ms";
    public const string AsrDecoderMs = "asr.decoder_ms";
    public const string AsrTotalMs = "asr.total_ms";
    public const string SearchMs = "search.query_ms";
    public const string EndOfSpeechToResultMs = "pipeline.end_of_speech_to_result_ms";
    /// <summary>End of speech to ANY feedback (result, "didn't catch that", or "not the owner's voice").</summary>
    public const string EndOfSpeechToOutcomeMs = "pipeline.end_of_speech_to_outcome_ms";
    public const string ModelInitPrefix = "model.init_ms.";
    public const string CpuPercent = "device.process_cpu_percent";
    public const string ManagedMemoryMb = "device.managed_memory_mb";
    public const string WorkingSetMb = "device.working_set_mb";

    public const string FramesCaptured = "audio.frames_captured";
    public const string FramesDropped = "audio.frames_dropped";
    public const string Discontinuities = "audio.discontinuities";
    public const string UtterancesDetected = "pipeline.utterances";
    public const string UtterancesNotOwner = "pipeline.utterances_not_owner";
    public const string UtterancesUncertain = "pipeline.utterances_uncertain";
    public const string RecognitionQueueDrops = "pipeline.recognition_queue_drops";
}

public sealed record MetricSummary(string Name, long Count, double Mean, double P50, double P95, double Max, double Last);

public sealed record TelemetrySnapshot(
    DateTimeOffset TakenAt, IReadOnlyList<MetricSummary> Metrics, IReadOnlyDictionary<string, long> Counters);

public interface IPerformanceTelemetry
{
    void Record(string metric, double value);

    void Increment(string counter, long delta = 1);

    TelemetrySnapshot Snapshot();

    void Reset();
}

/// <summary>
/// Thread-safe in-memory telemetry. Each metric keeps the last <see cref="WindowSize"/> values in a preallocated
/// ring, so recording does not allocate after the first value of a metric.
/// </summary>
public sealed class PerformanceTelemetry : IPerformanceTelemetry
{
    public const int WindowSize = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public PerformanceTelemetry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Record(string metric, double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        lock (_gate)
        {
            if (!_series.TryGetValue(metric, out var s))
            {
                s = new Series();
                _series[metric] = s;
            }

            s.Add(value);
        }
    }

    public void Increment(string counter, long delta = 1)
    {
        lock (_gate)
        {
            _counters[counter] = _counters.GetValueOrDefault(counter) + delta;
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            var metrics = _series.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.Summarize(p.Key)).ToList();
            return new TelemetrySnapshot(_time.GetUtcNow(), metrics, new Dictionary<string, long>(_counters));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _series.Clear();
            _counters.Clear();
        }
    }

    private sealed class Series
    {
        private readonly double[] _values = new double[WindowSize];
        private long _count;
        private double _max = double.MinValue;
        private double _last;

        public void Add(double v)
        {
            _values[(int)(_count % WindowSize)] = v;
            _count++;
            _last = v;
            if (v > _max)
            {
                _max = v;
            }
        }

        public MetricSummary Summarize(string name)
        {
            int n = (int)Math.Min(_count, WindowSize);
            var copy = new double[n];
            Array.Copy(_values, copy, n);
            Array.Sort(copy);
            double mean = n == 0 ? 0 : copy.Average();
            return new MetricSummary(name, _count, mean, Percentile(copy, 0.50), Percentile(copy, 0.95), _max, _last);
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            double rank = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
        }
    }
}
