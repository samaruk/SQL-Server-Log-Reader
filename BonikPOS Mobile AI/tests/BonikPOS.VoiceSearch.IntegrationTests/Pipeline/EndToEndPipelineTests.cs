using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Enrollment;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;

namespace BonikPOS.VoiceSearch.IntegrationTests.Pipeline;

/// <summary>
/// Enrolls an owner from two synthetic "voice A" clips with the real enrollment flow (real Silero VAD and GE2E speaker
/// encoder; audio replayed through <see cref="FileAudioCapture"/>), once for all tests of the class.
/// </summary>
public sealed class EnrolledOwnerFixture : IAsyncLifetime
{
    public BundledModelsFixture Models { get; } = new();

    public IReadOnlyList<EnrollmentSampleResult> Samples { get; private set; } = [];

    public VoiceProfile Profile { get; private set; } = null!;

    public OwnerVoiceReference Reference { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Models.InitializeAsync();
        await using var microphone = new ClipSequenceCapture(
            SolutionPaths.ReadPcm("synthetic-voiceA-1.wav"), SolutionPaths.ReadPcm("synthetic-voiceA-2.wav"));
        using var session = new EnrollmentSession(microphone, Models.Runtime, new EnrollmentOptions { RequiredSamples = 2, MaxSamples = 2 });
        var samples = new List<EnrollmentSampleResult>();
        foreach (var prompt in EnrollmentPrompts.Default.Take(2))
        {
            samples.Add(await session.RecordSampleAsync(prompt, level: null, CancellationToken.None, CancellationToken.None));
        }

        Samples = samples;
        Profile = session.Complete(new ConsentRecord(DateTimeOffset.UtcNow, "2026-10-v1"), "integration-test");
        Reference = OwnerProfileService.BuildReference(Profile, Models.Runtime.SpeakerEncoder!.Info, new OwnerVerificationSettings())
                    ?? throw new InvalidOperationException("The enrolled profile is not compatible with the installed encoder.");
    }

    public ValueTask DisposeAsync() => Models.DisposeAsync();
}

/// <summary>
/// End to end: FileAudioCapture (non-real-time) -> VoiceSearchPipeline with the real Silero VAD and GE2E speaker
/// encoder -> STUB recogniser returning fixed text -> medicine search over the demo catalog.
/// The clips are synthetic TTS voices used for plumbing only: these tests show that the components are wired together
/// correctly, not how accurately real voices are verified.
/// </summary>
public sealed class EndToEndPipelineTests(EnrolledOwnerFixture owner, ITestOutputHelper output) : IClassFixture<EnrolledOwnerFixture>
{
    private async Task<(PipelineProbe Probe, StubSpeechRecognizer Stub, PerformanceTelemetry Telemetry)> RunAsync(string clip, VoiceSearchSettings settings)
    {
        var stub = new StubSpeechRecognizer("napa extra 500");
        var runtime = new RuntimeWithStubRecognizer(owner.Models.Runtime, stub);
        var search = new MedicineSearchService(() => new DemoCatalogSource());
        await search.InitializeAsync(CancellationToken.None);
        var telemetry = new PerformanceTelemetry();
        await using var capture = new FileAudioCapture(SolutionPaths.ReadPcm(clip), realTime: false);
        await using var pipeline = new VoiceSearchPipeline(capture, runtime, search, telemetry);
        var probe = new PipelineProbe(pipeline);

        await pipeline.StartAsync(new PipelineSessionOptions { Settings = settings, Owner = owner.Reference }, CancellationToken.None);
        await probe.SessionEnded;

        Assert.Equal(PipelineState.Idle, pipeline.State);
        foreach (var e in probe.Events.Where(e => e is not AudioLevelEvent and not PipelineStateChangedEvent))
        {
            output.WriteLine(e switch
            {
                UtteranceIgnoredEvent i => $"ignored: {i.Verification.Decision} similarity {i.Verification.Similarity:F3} ({i.Reason})",
                SearchCompletedEvent s => $"searched: speaker {s.Verification.Decision} similarity {s.Verification.Similarity:F3}; " +
                                          $"decision {s.Search.Decision}; top {s.Search.Candidates.FirstOrDefault()?.Product.ProductId}",
                _ => e.ToString(),
            });
        }

        return (probe, stub, telemetry);
    }

    [Fact]
    public void Enrollment_FromTwoSyntheticVoiceAClips_ProducesAProfileForTheInstalledEncoder()
    {
        foreach (var s in owner.Samples)
        {
            output.WriteLine($"sample {s.Number}: accepted {s.Accepted}, speech {s.Quality.SpeechSeconds:F2} s at {s.Quality.SpeechDbfs:F1} dBFS, " +
                             $"consistency {s.ConsistencyWithOtherSamples:F3}, issues [{string.Join(", ", s.Issues)}]");
        }

        var embedding = Assert.Single(owner.Profile.Embeddings);
        output.WriteLine($"calibration: mean {embedding.MeanSampleSimilarity:F3}, min {embedding.MinSampleSimilarity:F3}, " +
                         $"suggested accept threshold {embedding.SuggestedAcceptThreshold:F3}; reference accept {owner.Reference.AcceptThreshold:F3} reject {owner.Reference.RejectThreshold:F3}");

        Assert.All(owner.Samples, s => Assert.True(s.Accepted, string.Join(", ", s.Issues)));
        Assert.Equal("ge2e-resemblyzer", embedding.EncoderId);
        Assert.Equal(256, embedding.Centroid.Length);
        Assert.Equal(2, embedding.SampleCount);
        Assert.Equal(ProfileCompatibility.Compatible, VoiceProfileCompatibility.Check(owner.Profile, owner.Models.Runtime.SpeakerEncoder!.Info));
        Assert.Equal(2, owner.Profile.Enrollment.AcceptedSamples);
        Assert.InRange(owner.Reference.AcceptThreshold, 0.68, 0.86); // bounded by the manifest's provisional limits
    }

    [Fact]
    public async Task HeldOutSyntheticVoiceAClip_PassesTheOwnerCheck_IsRecognisedByTheStub_AndSearched()
    {
        var (probe, stub, telemetry) = await RunAsync("synthetic-voiceA-3.wav", new VoiceSearchSettings());

        var done = Assert.Single(probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(OwnerVoiceDecision.Owner, done.Verification.Decision);
        Assert.Equal("ge2e-resemblyzer", done.Verification.EncoderId);
        Assert.Equal("napa extra 500", done.Recognition.Text);
        Assert.Equal(SearchDecision.Confident, done.Search.Decision);
        Assert.Equal("DEMO-0004", done.Search.Candidates[0].Product.ProductId); // Napa Extra 500 mg + 65 mg
        Assert.True(done.Search.Catalog.IsDemoData);
        Assert.False(done.Extraction.IsActive); // no extraction model installed: reported, not faked
        Assert.Single(stub.Calls);
        Assert.Empty(probe.Events.OfType<UtteranceIgnoredEvent>());
        Assert.Equal(1, telemetry.Snapshot().Counters.GetValueOrDefault(TelemetryMetrics.UtterancesDetected));
        Assert.Equal(0, telemetry.Snapshot().Counters.GetValueOrDefault(TelemetryMetrics.FramesDropped)); // non-real-time replay never drops
    }

    [Theory]
    [InlineData("synthetic-voiceB-1.wav")]
    [InlineData("synthetic-voiceB-2.wav")]
    public async Task SyntheticVoiceBClip_IsIgnored_WhenAnOwnerMatchIsRequired(string clip)
    {
        var (probe, stub, telemetry) = await RunAsync(clip, new VoiceSearchSettings());

        var ignored = Assert.Single(probe.Events.OfType<UtteranceIgnoredEvent>());
        Assert.Equal(OwnerVoiceDecision.NotOwner, ignored.Verification.Decision);
        Assert.True(ignored.Verification.Similarity < owner.Reference.RejectThreshold);
        Assert.Empty(probe.Events.OfType<SearchCompletedEvent>());
        Assert.Empty(stub.Calls); // never sent to speech recognition
        Assert.Equal(1, telemetry.Snapshot().Counters.GetValueOrDefault(TelemetryMetrics.UtterancesNotOwner));
    }

    [Fact]
    public async Task SyntheticVoiceBClip_WithOwnerMatchSwitchedOff_IsSearchedButNeverConfident()
    {
        var settings = new VoiceSearchSettings { Verification = new OwnerVerificationSettings { RequireOwnerMatch = false } };

        var (probe, stub, _) = await RunAsync("synthetic-voiceB-1.wav", settings);

        var done = Assert.Single(probe.Events.OfType<SearchCompletedEvent>());
        Assert.Equal(OwnerVoiceDecision.NotOwner, done.Verification.Decision);
        Assert.Equal("DEMO-0004", done.Search.Candidates[0].Product.ProductId);
        Assert.Equal(SearchDecision.NeedsConfirmation, done.Search.Decision);
        Assert.Single(stub.Calls);
    }
}
