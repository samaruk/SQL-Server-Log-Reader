using BonikPOS.VoiceSearch.AI.Recognition;
using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Pipeline;

/// <summary>
/// Orchestration of <see cref="VoiceSearchPipeline"/> with the real segmenter, frame queue and medicine search, and
/// fakes for the microphone, VAD (energy based), speaker verifier and a STUB recogniser returning fixed text.
/// </summary>
public sealed class VoiceSearchPipelineTests
{
    private static readonly OwnerVoiceReference Owner = new(new SpeakerEmbedding("test-encoder", "1", [1f, 0f]), 0.8, 0.72, 0.65);

    private static VoiceSearchSettings Settings(ListeningMode mode = ListeningMode.SingleQuery, bool requireOwner = true) => new()
    {
        Pipeline = new PipelineSettings { Mode = mode, QueueCapacityFrames = 2000 },
        Verification = new OwnerVerificationSettings { RequireOwnerMatch = requireOwner },
    };

    /// <summary>Silence, then one tone burst per entry of <paramref name="toneSeconds"/>, separated by 1.2 s of silence.</summary>
    private static float[] Speechlike(params double[] toneSeconds)
    {
        var parts = new List<float>(new float[8000]);
        foreach (double s in toneSeconds)
        {
            parts.AddRange(Signals.Sine(16000, 300, s, 0.3));
            parts.AddRange(new float[19200]);
        }

        return [.. parts];
    }

    private sealed class Harness
    {
        public required TestCapture Capture { get; init; }

        public required FakeRuntime Runtime { get; init; }

        public required PerformanceTelemetry Telemetry { get; init; }

        public required VoiceSearchPipeline Pipeline { get; init; }

        public required PipelineProbe Probe { get; init; }

        public long Counter(string name) => Telemetry.Snapshot().Counters.GetValueOrDefault(name);
    }

    private static async Task<Harness> CreateAsync(float[] signal, IOwnerVoiceVerifier? verifier, StubSpeechRecognizer? recognizer = null,
        bool releaseOnListening = true)
    {
        var capture = new TestCapture(signal);
        var runtime = new FakeRuntime { OwnerVerifier = verifier, Recognizer = recognizer ?? new StubSpeechRecognizer("napa extra 500") };
        var telemetry = new PerformanceTelemetry();
        var pipeline = new VoiceSearchPipeline(capture, runtime, await TestCatalogs.SearchServiceAsync(), telemetry);
        var probe = new PipelineProbe(pipeline);
        if (releaseOnListening)
        {
            bool released = false;
            probe.OnEvent = e =>
            {
                if (e is PipelineStateChangedEvent { State: PipelineState.Listening } && !released)
                {
                    released = true;
                    capture.Release();
                }
            };
        }

        return new Harness { Capture = capture, Runtime = runtime, Telemetry = telemetry, Pipeline = pipeline, Probe = probe };
    }

    [Fact]
    public async Task OwnerUtterance_IsVerifiedRecognisedAndSearched_ThenSingleQueryModeStops()
    {
        var verifier = new ScriptedVerifier(OwnerVoiceDecision.Owner);
        var recognizer = new StubSpeechRecognizer("napa extra 500", confidence: 0.9);
        var h = await CreateAsync(Speechlike(1.0), verifier, recognizer);

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var done = Assert.Single(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(OwnerVoiceDecision.Owner, done.Verification.Decision);
        Assert.Equal("napa extra 500", done.Recognition.Text);
        Assert.Equal(SearchDecision.Confident, done.Search.Decision);
        Assert.Equal("T-NAPA-EXTRA", done.Search.Candidates[0].Product.ProductId);
        Assert.Equal(QuerySource.Voice, done.Search.Query.Source);
        Assert.Equal(0.9, done.Search.Query.RecognitionConfidence);
        Assert.Equal(OwnerVoiceDecision.Owner, done.Search.Query.SpeakerDecision);
        Assert.False(done.Extraction.IsActive);

        // The verifier sees only speech; the recogniser gets speech plus pre-roll and trailing silence.
        Assert.True(verifier.SpeechLengths.TryPeek(out int speech));
        Assert.InRange(speech, 15000, 17500);
        Assert.True(recognizer.AudioLengths.TryPeek(out int audio));
        Assert.True(audio > speech);

        Assert.Equal(
            [PipelineState.Starting, PipelineState.Listening, PipelineState.HearingSpeech, PipelineState.Listening,
             PipelineState.Processing, PipelineState.Stopping, PipelineState.Idle],
            h.Probe.States);
        Assert.Equal(PipelineState.Idle, h.Pipeline.State);
        Assert.Equal(1, h.Counter(TelemetryMetrics.UtterancesDetected));
        Assert.True(h.Capture.StopCount >= 1);
        Assert.True(((EnergyVad)Assert.Single(h.Runtime.CreatedDetectors)).Disposed);
        Assert.Equal(1, h.Runtime.InitializeCount);
        Assert.Equal(20, h.Capture.LastOptions!.FrameMilliseconds);
        Assert.Equal(2000, h.Capture.LastOptions.QueueCapacityFrames);
    }

    [Fact]
    public async Task NotOwner_WithRequireOwnerMatch_IsIgnoredAndNeverRecognised()
    {
        var recognizer = new StubSpeechRecognizer("napa extra 500");
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.NotOwner, 0.3), recognizer);

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var ignored = Assert.Single(h.Probe.Events.OfType<UtteranceIgnoredEvent>());
        Assert.Equal(OwnerVoiceDecision.NotOwner, ignored.Verification.Decision);
        Assert.Empty(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.Empty(recognizer.AudioLengths);
        Assert.Equal(1, h.Counter(TelemetryMetrics.UtterancesNotOwner));
        Assert.Equal(PipelineState.Idle, h.Pipeline.State);
    }

    [Fact]
    public async Task NotOwner_WithoutRequireOwnerMatch_IsSearched_ButNeverPresentedAsConfident()
    {
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.NotOwner, 0.3));

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(requireOwner: false), Owner = Owner },
            TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var done = Assert.Single(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(OwnerVoiceDecision.NotOwner, done.Verification.Decision);
        Assert.NotEqual(SearchDecision.Confident, done.Search.Decision);
    }

    [Fact]
    public async Task UncertainSpeaker_IsSearched_ButTheDecisionIsCappedBelowConfident()
    {
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.Uncertain, 0.75));

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var done = Assert.Single(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(SearchDecision.NeedsConfirmation, done.Search.Decision);
        Assert.Equal("T-NAPA-EXTRA", done.Search.Candidates[0].Product.ProductId);
        Assert.Equal(1, h.Counter(TelemetryMetrics.UtterancesUncertain));
    }

    [Fact]
    public async Task InsufficientSpeech_IsIgnored()
    {
        var recognizer = new StubSpeechRecognizer("napa");
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.InsufficientSpeech), recognizer);

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        Assert.Equal(OwnerVoiceDecision.InsufficientSpeech, Assert.Single(h.Probe.Events.OfType<UtteranceIgnoredEvent>()).Verification.Decision);
        Assert.Empty(recognizer.AudioLengths);
    }

    [Fact]
    public async Task WithoutAnOwnerProfile_SpeechIsSearchedAsNoProfile_AndCappedBelowConfident()
    {
        var verifier = new ScriptedVerifier(OwnerVoiceDecision.Owner);
        var h = await CreateAsync(Speechlike(1.0), verifier);

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = null }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var done = Assert.Single(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(OwnerVoiceDecision.NoProfile, done.Verification.Decision);
        Assert.Equal(SearchDecision.NeedsConfirmation, done.Search.Decision);
        Assert.Empty(verifier.SpeechLengths); // no reference -> the verifier is not consulted
    }

    [Fact]
    public async Task WithoutASpeakerModel_TheDecisionIsModelUnavailable_AndCapped()
    {
        var h = await CreateAsync(Speechlike(1.0), verifier: null);

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var done = Assert.Single(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(OwnerVoiceDecision.ModelUnavailable, done.Verification.Decision);
        Assert.Equal(SearchDecision.NeedsConfirmation, done.Search.Decision);
    }

    [Fact]
    public async Task MissingSpeechRecognitionModel_RaisesAFatalErrorAndStops()
    {
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.Owner));
        h.Runtime.Recognizer = new UnavailableSpeechRecognizer("No speech-recognition model is installed.");

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(ListeningMode.Continuous), Owner = Owner },
            TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var error = Assert.Single(h.Probe.Events.OfType<PipelineErrorEvent>());
        Assert.True(error.Fatal);
        Assert.Contains("No speech-recognition model", error.Message);
        Assert.Contains(PipelineState.Stopping, h.Probe.States);
        Assert.Empty(h.Probe.Events.OfType<SearchCompletedEvent>());
    }

    [Fact]
    public async Task EmptyRecognition_RaisesNothingRecognised_AndKeepsListening()
    {
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.Owner), new StubSpeechRecognizer("  "));

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(), Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        Assert.Single(h.Probe.Events.OfType<NothingRecognizedEvent>());
        Assert.Empty(h.Probe.Events.OfType<SearchCompletedEvent>());
        Assert.DoesNotContain(PipelineState.Stopping, h.Probe.States); // single-query mode waits for a real result
    }

    [Fact]
    public async Task ContinuousMode_ProcessesEveryUtteranceUntilTheStreamEnds()
    {
        var recognizer = new StubSpeechRecognizer("seclo 20 mg");
        double[] tones = [0.8, 1.0, 0.9];
        var h = await CreateAsync(Speechlike(tones), new ScriptedVerifier(OwnerVoiceDecision.Owner), recognizer, releaseOnListening: false);

        // Each tone is released only after the previous search finished, as a live microphone would deliver it, so the
        // bounded recognition queue (2 utterances, drop-oldest) never overflows.
        var ends = new List<int>();
        int position = 8000;
        foreach (double t in tones)
        {
            position += (int)Math.Round(16000 * t) + 19200;
            ends.Add(position);
        }

        int searches = 0;
        h.Probe.OnEvent = e =>
        {
            if (e is PipelineStateChangedEvent { State: PipelineState.Listening } && searches == 0 && h.Capture.Frames.GetStatistics().FramesWritten == 0)
            {
                h.Capture.ReleaseUntil(ends[0]);
            }
            else if (e is SearchCompletedEvent)
            {
                searches++;
                h.Capture.ReleaseUntil(ends[Math.Min(searches, ends.Count - 1)], complete: searches == ends.Count - 1);
            }
        };

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings(ListeningMode.Continuous), Owner = Owner },
            TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        var results = h.Probe.Events.OfType<SearchCompletedEvent>().ToList();
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("T-SECLO-20", r.Search.Candidates[0].Product.ProductId));
        Assert.Equal(3, recognizer.AudioLengths.Count);
        Assert.Equal(3, h.Counter(TelemetryMetrics.UtterancesDetected));
    }

    [Fact]
    public async Task SingleQueryMode_StopsAfterTheListenTimeoutWhenNobodySpeaks()
    {
        var h = await CreateAsync(new float[16000 * 3], new ScriptedVerifier(OwnerVoiceDecision.Owner));
        var settings = Settings() with { Pipeline = Settings().Pipeline with { MaxListenMilliseconds = 400 } };

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = settings, Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        Assert.Single(h.Probe.Events.OfType<ListeningTimedOutEvent>());
        Assert.Contains(PipelineState.Stopping, h.Probe.States);
        Assert.True(h.Counter(TelemetryMetrics.FramesCaptured) > 400 / 20);
    }

    [Fact]
    public async Task CaptureOverrun_IsReportedAsADiscontinuity_AndResetsVadAndExtractor()
    {
        var signal = Speechlike(1.0);
        var h = await CreateAsync(signal, new ScriptedVerifier(OwnerVoiceDecision.Owner), releaseOnListening: false);
        var extractor = new RecordingExtractor();
        h.Runtime.ExtractorFactory = () => extractor;
        int frames = signal.Length / 320;
        // Fill a 4-frame queue before the pipeline reads anything: all but the last 4 frames are dropped. The stream is
        // completed once the pipeline reports Listening.
        h.Capture.StateChanged += (_, e) =>
        {
            if (e.State == MicrophoneCaptureState.Running)
            {
                h.Capture.ReleaseUntil(signal.Length);
            }
        };
        h.Probe.OnEvent = e =>
        {
            if (e is PipelineStateChangedEvent { State: PipelineState.Listening })
            {
                ((AudioFrameQueue)h.Capture.Frames).Complete();
            }
        };
        var settings = Settings() with { Pipeline = Settings().Pipeline with { QueueCapacityFrames = 4 } };

        await h.Pipeline.StartAsync(new PipelineSessionOptions { Settings = settings, Owner = Owner }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        Assert.Equal(1, h.Counter(TelemetryMetrics.Discontinuities));
        Assert.Equal(frames - 4, h.Counter(TelemetryMetrics.FramesDropped));
        Assert.Equal(4, h.Counter(TelemetryMetrics.FramesCaptured));
        Assert.Equal(1, ((EnergyVad)h.Runtime.CreatedDetectors[0]).ResetCount);
        Assert.Equal(1, extractor.ResetCount);
        Assert.True(extractor.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtractionTarget_IsOnlyPassedToTheExtractorWhenEnabled(bool enabled)
    {
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.Owner));
        var extractor = new RecordingExtractor();
        h.Runtime.ExtractorFactory = () => extractor;
        var target = new SpeakerEmbedding("tse-encoder", "1", [0.6f, 0.8f]);

        await h.Pipeline.StartAsync(new PipelineSessionOptions
        {
            Settings = Settings() with { TargetSpeakerExtractionEnabled = enabled },
            Owner = Owner,
            ExtractionTarget = target,
        }, TestContext.Current.CancellationToken);
        await h.Probe.SessionEndedAsync();

        Assert.Equal(enabled ? target : null, Assert.Single(extractor.Targets));
        Assert.True(extractor.ProcessedSamples > 0);
    }

    [Fact]
    public async Task StartingTwice_IsRejected_AndStopWhileIdleIsANoOp()
    {
        var h = await CreateAsync(Speechlike(1.0), new ScriptedVerifier(OwnerVoiceDecision.Owner), releaseOnListening: false);
        await h.Pipeline.StopAsync(TestContext.Current.CancellationToken); // idle: nothing to stop

        var options = new PipelineSessionOptions { Settings = Settings(), Owner = Owner };
        await h.Pipeline.StartAsync(options, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Pipeline.StartAsync(options, TestContext.Current.CancellationToken));

        await h.Pipeline.StopAsync(TestContext.Current.CancellationToken); // stops a session that never heard speech
        Assert.Equal(PipelineState.Idle, h.Pipeline.State);

        // A stopped pipeline can be started again.
        await h.Pipeline.StartAsync(options, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineState.Listening, h.Pipeline.State);
        await h.Pipeline.DisposeAsync();
        Assert.Equal(PipelineState.Idle, h.Pipeline.State);
        Assert.Equal(2, h.Capture.StartCount);
    }

    /// <summary>
    /// A capture that ends at once (e.g. the device failed right after starting) must still leave a pipeline that
    /// reports Idle and can be started again. Before the fix, the workers could finish the session before StartAsync
    /// had stored it and set Listening: events arrived as Starting, Idle, Listening and the pipeline then stayed in
    /// Listening/Stopping for good. The race window is small, so the scenario is repeated many times.
    /// </summary>
    [Fact]
    public async Task ASessionThatEndsImmediately_AlwaysReturnsToIdle_AndCanBeRestarted()
    {
        var search = await TestCatalogs.SearchServiceAsync();
        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < 150; i++)
        {
            var capture = new TestCapture([]);
            capture.StateChanged += (_, e) =>
            {
                if (e.State == MicrophoneCaptureState.Running)
                {
                    capture.Release(); // no audio at all: the stream completes immediately
                }
            };
            var pipeline = new VoiceSearchPipeline(capture, new FakeRuntime(), search, new PerformanceTelemetry());
            var probe = new PipelineProbe(pipeline);

            await pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings() }, ct);
            await probe.WaitForAsync(e => e is PipelineStateChangedEvent { State: PipelineState.Idle });
            await pipeline.StopAsync(ct);

            Assert.Equal([PipelineState.Starting, PipelineState.Listening, PipelineState.Idle], probe.States);
            Assert.Equal(PipelineState.Idle, pipeline.State);
            await pipeline.StartAsync(new PipelineSessionOptions { Settings = Settings() }, ct); // must not throw
            await pipeline.DisposeAsync();
            Assert.Equal(PipelineState.Idle, pipeline.State);
        }
    }

    private sealed class RecordingExtractor : ITargetSpeakerExtractor
    {
        public List<SpeakerEmbedding?> Targets { get; } = [];

        public int ResetCount { get; private set; }

        public long ProcessedSamples { get; private set; }

        public bool Disposed { get; private set; }

        public TargetSpeakerExtractorStatus Status { get; } =
            new(TargetSpeakerExtractionAvailability.NotInstalled, null, null, 0, 0, "recording pass-through (test)");

        public void SetTarget(SpeakerEmbedding? target) => Targets.Add(target);

        public void Process(ReadOnlySpan<float> input, Span<float> output)
        {
            ProcessedSamples += input.Length;
            input.CopyTo(output);
        }

        public void Reset() => ResetCount++;

        public void Dispose() => Disposed = true;
    }
}
