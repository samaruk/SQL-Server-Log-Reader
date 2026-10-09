using System.Diagnostics;
using System.Globalization;
using BonikPOS.VoiceSearch.Core.Benchmark;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.Audio.Benchmark;

/// <summary>
/// Replays clips through the real <see cref="VoiceSearchPipeline"/> (VAD end-pointing, optional target-speaker
/// extraction, owner verification, speech recognition, medicine search) with the models installed on this device, in
/// real time by default, and reports latency, throughput and resource readings. The owner reference is built in memory
/// from the clips marked for enrollment and discarded afterwards; no audio or text is stored or reported.
/// </summary>
public sealed class PipelineBenchmark(
    IVoiceModelRuntime runtime,
    IMedicineSearchService search,
    IResourceSampler sampler,
    Func<CancellationToken, Task<VoiceSearchSettings>> loadSettings,
    ILogger<PipelineBenchmark>? logger = null) : IPipelineBenchmark
{
    private static readonly TimeSpan ClipTimeout = TimeSpan.FromSeconds(120);
    private readonly ILogger _logger = logger ?? NullLogger<PipelineBenchmark>.Instance;

    public async Task<PipelineBenchmarkReport> RunAsync(
        IReadOnlyList<BenchmarkClip> clips, PipelineBenchmarkOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(options);
        if (!clips.Any(c => !c.UseForEnrollment))
        {
            throw new ArgumentException("At least one clip must be a test clip (not only enrollment).", nameof(clips));
        }

        var started = DateTimeOffset.UtcNow;
        var baseSettings = await loadSettings(cancellationToken).ConfigureAwait(false);
        AcceleratorPreference[] accelerators = options.CompareAccelerators
            ? [AcceleratorPreference.CpuOnly, AcceleratorPreference.Auto]
            : [baseSettings.Accelerator];
        var notes = new List<string>
        {
            "Clips are replayed through FileAudioCapture " + (options.RealTime ? "in real time (10 ms callbacks)" : "as fast as possible") +
            "; the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).",
            $"End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is {baseSettings.Pipeline.EndSilenceMilliseconds} ms of silence after the last word.",
            $"ONNX Runtime threads: per-utterance models {(baseSettings.InferenceThreads > 0 ? baseSettings.InferenceThreads.ToString(CultureInfo.InvariantCulture) : "auto (min(4, cores))")}, " +
            $"streaming models {(baseSettings.StreamingModelThreads > 0 ? baseSettings.StreamingModelThreads.ToString(CultureInfo.InvariantCulture) : "same")}; " +
            $"worker spinning {(baseSettings.InferenceThreadSpinning ? "on" : "off")}.",
        };
        var runs = new List<AcceleratorRun>();
        try
        {
            foreach (var accelerator in accelerators)
            {
                progress?.Report($"Loading models ({accelerator})...");
                var settings = baseSettings with
                {
                    Accelerator = accelerator,
                    Pipeline = baseSettings.Pipeline with { Mode = ListeningMode.SingleQuery, ParallelVerification = options.ParallelVerification },
                };
                await runtime.ReloadAsync(settings, cancellationToken).ConfigureAwait(false);
                notes.AddRange(runtime.Status.Messages.Select(m => $"Runtime ({accelerator}): {m}"));
                if (!runtime.Status.VoiceActivity.Available)
                {
                    throw new InvalidOperationException("The voice activity model is not loaded, so the pipeline cannot run: " +
                        runtime.Status.VoiceActivity.Description + " " + string.Join(" ", runtime.Status.Messages));
                }

                runs.Add(await RunWithAsync(clips, options, settings, accelerator, progress, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            if (accelerators.Length > 1 || accelerators[0] != baseSettings.Accelerator)
            {
                await runtime.ReloadAsync(baseSettings, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (runs.Any(r => r.Models.Any(m => m.ExecutionProvider is not null and not "CPU")) is false && options.CompareAccelerators)
        {
            notes.Add("No accelerator execution provider was used by any model in the Auto run (not available on this platform or rejected by the models); both runs used the CPU.");
        }

        return new PipelineBenchmarkReport("bonikpos-pipeline-bench/1", options.Label, started, DeviceDescription(),
            PipelineBenchmarkReport.DescribeRuntime(), Environment.ProcessorCount, options, runs, notes);
    }

    /// <summary>Overridable by the app to include the device model name.</summary>
    public Func<string> DeviceDescription { get; init; } = () => PipelineBenchmarkReport.DescribeRuntime();

    private async Task<AcceleratorRun> RunWithAsync(
        IReadOnlyList<BenchmarkClip> clips, PipelineBenchmarkOptions options, VoiceSearchSettings settings,
        AcceleratorPreference accelerator, IProgress<string>? progress, CancellationToken token)
    {
        var status = runtime.Status;
        var models = new List<ModelUse>
        {
            Use("VoiceActivity", status.VoiceActivity),
            Use("SpeakerEncoder", status.SpeakerEncoder),
            Use("TargetSpeakerExtraction", status.TargetSpeakerExtraction),
            Use("SpeechRecognition", status.SpeechRecognition),
        };
        double warmUp = 0;
        if (options.WarmUp)
        {
            progress?.Report("Warming up models...");
            warmUp = await runtime.WarmUpAsync(token).ConfigureAwait(false);
        }

        var owner = BuildOwner(clips, settings, token);
        var extractionTarget = owner is not null && runtime.ExtractionRequiredEncoderId == owner.Centroid.EncoderId ? owner.Centroid : null;
        var telemetry = new PerformanceTelemetry();
        var runsOut = new List<ClipRun>();
        var before = sampler.Sample();
        using var monitor = new PeakMonitor(sampler);
        var wall = Stopwatch.StartNew();
        var tests = clips.Where(c => !c.UseForEnrollment).ToList();
        for (int rep = 1; rep <= options.Repetitions; rep++)
        {
            foreach (var clip in tests)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"{accelerator}: {clip.Name} ({rep}/{options.Repetitions})");
                runsOut.Add(await RunClipAsync(clip, rep, accelerator.ToString(), settings, owner, extractionTarget, telemetry, options.RealTime, token)
                    .ConfigureAwait(false));
            }
        }

        wall.Stop();
        var after = sampler.Sample();
        var snapshot = telemetry.Snapshot();
        double? cpuOneCore = before.ProcessCpuMilliseconds is double c0 && after.ProcessCpuMilliseconds is double c1
            ? (c1 - c0) / wall.Elapsed.TotalMilliseconds * 100
            : null;
        string[] wanted =
        [
            TelemetryMetrics.CaptureToProcessedMs, TelemetryMetrics.FrameProcessingMs, TelemetryMetrics.TseChunkMs,
            TelemetryMetrics.SpeakerVerifyMs, TelemetryMetrics.AsrTotalMs, TelemetryMetrics.SearchMs, TelemetryMetrics.EndOfSpeechToResultMs,
            TelemetryMetrics.EndOfSpeechToOutcomeMs,
        ];
        var metrics = snapshot.Metrics.Where(m => wanted.Contains(m.Name))
            .Select(m => new BenchmarkMetric(m.Name, m.Count, m.P50, m.P95, m.Max, m.Mean)).ToList();
        return new AcceleratorRun(
            accelerator.ToString(), models, warmUp, runsOut, metrics,
            snapshot.Counters.GetValueOrDefault(TelemetryMetrics.FramesCaptured),
            snapshot.Counters.GetValueOrDefault(TelemetryMetrics.FramesDropped),
            snapshot.Counters.GetValueOrDefault(TelemetryMetrics.Discontinuities),
            snapshot.Counters.GetValueOrDefault(TelemetryMetrics.RecognitionQueueDrops),
            wall.Elapsed.TotalSeconds, cpuOneCore, cpuOneCore / Environment.ProcessorCount,
            monitor.PeakManagedMb, monitor.PeakWorkingSetMb, before, after, monitor.MeanBatteryCurrent,
            monitor.MeanBatteryPowerMilliwatts, monitor.MaxBatteryTemperature);
    }

    private async Task<ClipRun> RunClipAsync(
        BenchmarkClip clip, int repetition, string accelerator, VoiceSearchSettings settings, OwnerVoiceReference? owner,
        SpeakerEmbedding? extractionTarget, IPerformanceTelemetry telemetry, bool realTime, CancellationToken token)
    {
        var capture = new FileAudioCapture(new PcmAudio(clip.Samples16k, 16000, 1), realTime);
        await using var pipeline = new VoiceSearchPipeline(capture, runtime, search, telemetry);
        var done = new TaskCompletionSource<ClipRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        double speechMs = 0;
        pipeline.EventRaised += (_, e) =>
        {
            switch (e.Event)
            {
                case UtteranceCapturedEvent u:
                    speechMs = u.SpeechMilliseconds;
                    break;
                case SearchCompletedEvent s:
                    var r = s.Recognition;
                    done.TrySetResult(new ClipRun(clip.Name, repetition, accelerator, $"result:{s.Search.Decision}", speechMs,
                        s.Timings.VerificationMs, s.Timings.RecognitionMs, r.FeatureTime.TotalMilliseconds, r.EncoderTime.TotalMilliseconds,
                        r.DecoderTime.TotalMilliseconds, r.TokenCount, s.Timings.SearchMs, s.Timings.EndOfSpeechToResultMs,
                        s.Timings.EndOfSpeechToResultMs + settings.Pipeline.EndSilenceMilliseconds, s.Timings.EndOfSpeechToResultMs));
                    break;
                case UtteranceIgnoredEvent ig:
                    done.TrySetResult(new ClipRun(clip.Name, repetition, accelerator, $"ignored:{ig.Verification.Decision}", speechMs,
                        ig.Verification.Elapsed.TotalMilliseconds, null, null, null, null, null, null, null, null, ig.EndOfSpeechToOutcomeMs));
                    break;
                case NothingRecognizedEvent n:
                    done.TrySetResult(new ClipRun(clip.Name, repetition, accelerator, "nothing-recognized", speechMs, null, null,
                        n.Recognition.FeatureTime.TotalMilliseconds, n.Recognition.EncoderTime.TotalMilliseconds,
                        n.Recognition.DecoderTime.TotalMilliseconds, n.Recognition.TokenCount, null, null, null, n.EndOfSpeechToOutcomeMs));
                    break;
                case ListeningTimedOutEvent:
                    done.TrySetResult(new ClipRun(clip.Name, repetition, accelerator, "no-speech-detected", 0, null, null, null, null, null, null, null, null, null));
                    break;
                case PipelineErrorEvent err:
                    done.TrySetResult(new ClipRun(clip.Name, repetition, accelerator, "error:" + err.Message, speechMs, null, null, null, null, null, null, null, null, null));
                    break;
                case PipelineStateChangedEvent { State: PipelineState.Idle or PipelineState.Faulted } st when !done.Task.IsCompleted:
                    // Capture ended (file finished) without any outcome event.
                    done.TrySetResult(new ClipRun(clip.Name, repetition, accelerator, "ended:" + (st.Detail ?? "no utterance"), speechMs, null, null, null, null, null, null, null, null, null));
                    break;
            }
        };

        await pipeline.StartAsync(new PipelineSessionOptions { Settings = settings, Owner = owner, ExtractionTarget = extractionTarget }, token)
            .ConfigureAwait(false);
        ClipRun result;
        try
        {
            result = await done.Task.WaitAsync(ClipTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            result = new ClipRun(clip.Name, repetition, accelerator, "timeout", speechMs, null, null, null, null, null, null, null, null, null);
        }

        await pipeline.StopAsync(token).ConfigureAwait(false);
        return result;
    }

    /// <summary>Temporary in-memory owner reference from the enrollment clips (same thresholds as a fresh profile).</summary>
    private OwnerVoiceReference? BuildOwner(IReadOnlyList<BenchmarkClip> clips, VoiceSearchSettings settings, CancellationToken token)
    {
        var encoder = runtime.SpeakerEncoder;
        var enrol = clips.Where(c => c.UseForEnrollment).ToList();
        if (encoder is null || enrol.Count == 0)
        {
            _logger.LogInformation("Benchmark runs without an owner reference (no encoder or no enrollment clips).");
            return null;
        }

        var sum = new double[encoder.Info.EmbeddingDimension];
        foreach (var clip in enrol)
        {
            var e = encoder.EmbedUtterance(clip.Samples16k, token);
            for (int i = 0; i < sum.Length; i++)
            {
                sum[i] += e.Vector[i];
            }
        }

        var centroid = new SpeakerEmbedding(encoder.Info.ModelId, encoder.Info.ModelVersion, [.. sum.Select(v => (float)v)]);
        double accept = settings.Verification.AcceptThresholdOverride ?? encoder.Info.Calibration.DefaultAcceptThreshold;
        return new OwnerVoiceReference(centroid, accept, accept - settings.Verification.UncertainBand, accept - settings.Verification.WindowDrop);
    }

    private static ModelUse Use(string role, ComponentStatus s) => new(role, s.ModelId, s.ExecutionProvider, s.InitMilliseconds, s.Available);

    /// <summary>Samples memory, battery current, voltage and temperature (where available) every 200 ms during the run.</summary>
    private sealed class PeakMonitor : IDisposable
    {
        private readonly IResourceSampler _sampler;
        private readonly Timer _timer;
        private readonly Lock _gate = new();
        private double _currentSum;
        private int _currentCount;
        private double _powerSum;
        private int _powerCount;

        public PeakMonitor(IResourceSampler sampler)
        {
            _sampler = sampler;
            _timer = new Timer(_ => Tick(), null, 0, 200);
        }

        public double PeakManagedMb { get; private set; }

        public double? PeakWorkingSetMb { get; private set; }

        public double? MeanBatteryCurrent
        {
            get
            {
                lock (_gate)
                {
                    return _currentCount == 0 ? null : _currentSum / _currentCount;
                }
            }
        }

        /// <summary>Mean of |current| x voltage: whole-device power drawn from the battery (meaningful only when unplugged).</summary>
        public double? MeanBatteryPowerMilliwatts
        {
            get
            {
                lock (_gate)
                {
                    return _powerCount == 0 ? null : _powerSum / _powerCount;
                }
            }
        }

        public double? MaxBatteryTemperature { get; private set; }

        public void Dispose()
        {
            _timer.Dispose();
            Tick();
        }

        private void Tick()
        {
            var s = _sampler.Sample();
            lock (_gate)
            {
                PeakManagedMb = Math.Max(PeakManagedMb, s.ManagedMemoryMb);
                if (s.WorkingSetMb is double ws)
                {
                    PeakWorkingSetMb = Math.Max(PeakWorkingSetMb ?? 0, ws);
                }

                if (s.BatteryCurrentMicroamps is double ua)
                {
                    _currentSum += ua;
                    _currentCount++;
                    if (s.BatteryVoltageMillivolts is double mv)
                    {
                        _powerSum += Math.Abs(ua) * mv / 1e6; // uA x mV = 1e-6 mW
                        _powerCount++;
                    }
                }

                if (s.BatteryTemperatureCelsius is double t)
                {
                    MaxBatteryTemperature = Math.Max(MaxBatteryTemperature ?? double.MinValue, t);
                }
            }
        }
    }
}
