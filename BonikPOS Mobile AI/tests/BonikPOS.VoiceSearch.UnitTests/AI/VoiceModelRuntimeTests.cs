using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.AI;

public sealed class VoiceModelRuntimeTests(GeneratedModels models) : IClassFixture<GeneratedModels>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private FakeModelProvider FixtureProvider() => new FakeModelProvider()
        .With(ModelRole.SpeakerEncoder, FakeModelProvider.Model("enc-fixture", ModelRole.SpeakerEncoder, "fbank-speaker-onnx",
            new Dictionary<string, string> { ["model"] = models.FbankEncoder }, """{"embedding_dim": 80}""", isTestFixture: true))
        .With(ModelRole.TargetSpeakerExtraction, FakeModelProvider.Model("tse-fixture", ModelRole.TargetSpeakerExtraction, "streaming-tse-onnx/1",
            new Dictionary<string, string> { ["model"] = models.Tse },
            """{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-fixture", "states": [{"input": "state_in", "output": "state_out"}]}""",
            isTestFixture: true));

    [Fact]
    public async Task ModelsMarkedAsTestFixtures_AreNeverLoaded_UnlessExplicitlyAllowed()
    {
        var provider = FixtureProvider();
        using var runtime = new OnnxVoiceModelRuntime(provider, new PerformanceTelemetry());

        await runtime.InitializeAsync(new VoiceSearchSettings(), Ct);

        Assert.Null(runtime.SpeakerEncoder);
        Assert.Null(runtime.OwnerVerifier);
        Assert.False(runtime.Status.SpeakerEncoder.Available);
        Assert.Contains("only a test fixture", runtime.Status.SpeakerEncoder.Description);
        Assert.False(runtime.Status.TargetSpeakerExtraction.Available);
        Assert.Equal(TargetSpeakerExtractionAvailability.NotInstalled, runtime.CreateTargetSpeakerExtractorStatusPreview().Availability);
        Assert.Contains(runtime.Status.Messages, m => m.Contains("'enc-fixture' is a test fixture"));
        Assert.Contains(runtime.Status.Messages, m => m.Contains("'tse-fixture' is a test fixture"));
        Assert.Empty(provider.Failures); // refused before any load attempt, not a load failure
    }

    [Fact]
    public async Task ModelsMarkedAsTestFixtures_LoadWhenTheTestOnlyFlagIsSet()
    {
        using var runtime = new OnnxVoiceModelRuntime(FixtureProvider(), new PerformanceTelemetry(), allowTestFixtures: true);

        await runtime.InitializeAsync(new VoiceSearchSettings(), Ct);

        Assert.NotNull(runtime.SpeakerEncoder);
        Assert.Equal("enc-fixture", runtime.SpeakerEncoder!.Info.ModelId);
        Assert.Equal(80, runtime.SpeakerEncoder.Info.EmbeddingDimension);
        Assert.NotNull(runtime.OwnerVerifier);
        Assert.True(runtime.Status.TargetSpeakerExtraction.Available, runtime.Status.TargetSpeakerExtraction.Description);
        Assert.Equal("enc-fixture", runtime.ExtractionRequiredEncoderId);
    }

    [Fact]
    public async Task WithoutModels_EveryComponentIsReportedUnavailable_AndNothingIsFaked()
    {
        using var runtime = new OnnxVoiceModelRuntime(new FakeModelProvider(), new PerformanceTelemetry());
        Assert.False(runtime.Status.IsInitialized);
        Assert.False(runtime.Recognizer.Info.IsAvailable);

        await runtime.InitializeAsync(new VoiceSearchSettings(), Ct);

        Assert.True(runtime.Status.IsInitialized);
        Assert.False(runtime.Status.VoiceActivity.Available);
        Assert.False(runtime.Status.SpeakerEncoder.Available);
        Assert.False(runtime.Status.TargetSpeakerExtraction.Available);
        Assert.False(runtime.Status.SpeechRecognition.Available);
        Assert.Throws<InvalidOperationException>(() => runtime.CreateVoiceActivityDetector());
        Assert.Null(runtime.SpeakerEncoder);
        Assert.Null(runtime.OwnerVerifier);
        Assert.False(runtime.CreateTargetSpeakerExtractor().Status.IsActive);
        Assert.False(runtime.Recognizer.Info.IsAvailable);
        await Assert.ThrowsAsync<SpeechRecognitionUnavailableException>(() =>
            runtime.Recognizer.RecognizeAsync(new float[16000], new SpeechRecognitionOptions(), Ct));
    }

    [Fact]
    public async Task SpeakerEncoderWithAnUnknownFormat_IsReportedAsIncompatible()
    {
        var provider = new FakeModelProvider().With(ModelRole.SpeakerEncoder, FakeModelProvider.Model("enc", ModelRole.SpeakerEncoder,
            "mystery-format", new Dictionary<string, string> { ["model"] = models.FbankEncoder }, """{"embedding_dim": 80}"""));
        using var runtime = new OnnxVoiceModelRuntime(provider, new PerformanceTelemetry());

        await runtime.InitializeAsync(new VoiceSearchSettings(), Ct);

        Assert.Null(runtime.SpeakerEncoder);
        Assert.Contains("mystery-format", runtime.Status.SpeakerEncoder.Description);
        Assert.Equal(ModelState.IncompatibleContract, Assert.Single(provider.Failures).State);
    }
}

/// <summary>The Whisper adapter must report itself unavailable (never a fake recogniser) when its files are missing or broken.</summary>
public sealed class WhisperAvailabilityTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Dictionary<string, string> Files(string root) => new()
    {
        ["config"] = Path.Combine(root, "asr", "w-config.json"),
        ["encoder"] = Path.Combine(root, "asr", "w-encoder.onnx"),
        ["decoder"] = Path.Combine(root, "asr", "w-decoder.onnx"),
        ["tokens"] = Path.Combine(root, "asr", "w-tokens.txt"),
    };

    private static async Task<OnnxVoiceModelRuntime> InitializeAsync(IModelProvider provider)
    {
        var runtime = new OnnxVoiceModelRuntime(provider, new PerformanceTelemetry());
        await runtime.InitializeAsync(new VoiceSearchSettings(), CancellationToken.None);
        return runtime;
    }

    private static async Task AssertRecognizerUnavailableAsync(OnnxVoiceModelRuntime runtime, string expected)
    {
        Assert.False(runtime.Recognizer.Info.IsAvailable);
        Assert.False(runtime.Status.SpeechRecognition.Available);
        Assert.Contains(expected, runtime.Recognizer.Info.StatusMessage, StringComparison.OrdinalIgnoreCase);
        var ex = await Assert.ThrowsAsync<SpeechRecognitionUnavailableException>(() =>
            runtime.Recognizer.RecognizeAsync(new float[16000], new SpeechRecognitionOptions(), CancellationToken.None));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestEntryWhoseFilesAreMissing_IsNotInstalled()
    {
        File.WriteAllText(_dir.File("models.json"), """
            {"format": "bonikpos-models/1", "models": [{"id": "whisper-small", "version": "1", "role": "SpeechRecognition",
              "format": "bonikpos-whisper-onnx/1", "files": {"config": "asr/w-config.json", "encoder": "asr/w-encoder.onnx",
              "decoder": "asr/w-decoder.onnx", "tokens": "asr/w-tokens.txt"}}]}
            """);
        var provider = new FileSystemModelProvider([_dir.Path], checksumCachePath: null);

        var status = (await provider.GetStatusAsync(CancellationToken.None)).Single(s => s.Role == ModelRole.SpeechRecognition);
        Assert.Equal(ModelState.NotInstalled, status.State);
        Assert.Contains("Missing asr/w-encoder.onnx", status.Message);

        using var runtime = await InitializeAsync(provider);
        await AssertRecognizerUnavailableAsync(runtime, "not installed");
    }

    [Fact]
    public async Task FilesThatDisappearAfterVerification_MakeTheRecognizerUnavailable()
    {
        var provider = new FakeModelProvider().With(ModelRole.SpeechRecognition,
            FakeModelProvider.Model("whisper-gone", ModelRole.SpeechRecognition, "bonikpos-whisper-onnx/1", Files(_dir.Path)));

        using var runtime = await InitializeAsync(provider);

        await AssertRecognizerUnavailableAsync(runtime, "failed to load");
        Assert.Equal(ModelState.LoadFailed, Assert.Single(provider.Failures).State);
    }

    /// <summary>A structurally valid "bonikpos-whisper-onnx/1" config (tiny dimensions).</summary>
    private const string ValidConfig = """
        {"format": "bonikpos-whisper-onnx/1", "model_id": "w", "license": "n/a", "source": "unit test", "quantization": "none",
         "dims": {"n_audio_ctx_exported": 150, "n_text_layer": 2, "n_text_state": 32, "n_text_ctx": 448, "n_vocab": 51865},
         "features": {"sample_rate": 16000, "n_fft": 400, "hop_length": 160, "n_mels": 80, "n_frames": 300},
         "special_tokens": {"eot": 50257, "sot": 50258, "no_speech": 50362, "no_timestamps": 50363, "transcribe": 50359, "timestamp_begin": 50364},
         "languages": {"en": 50259, "bn": 50302}, "suppress_tokens": [1, 2], "suppress_at_begin": [220, 50257]}
        """;

    [Fact]
    public async Task ManifestWithoutARequiredFileEntry_MakesTheRecognizerUnavailable()
    {
        var files = Files(_dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(files["config"])!);
        File.WriteAllText(files["config"], ValidConfig);
        files.Remove("tokens");
        var provider = new FakeModelProvider().With(ModelRole.SpeechRecognition,
            FakeModelProvider.Model("whisper-partial", ModelRole.SpeechRecognition, "bonikpos-whisper-onnx/1", files));

        using var runtime = await InitializeAsync(provider);

        await AssertRecognizerUnavailableAsync(runtime, "tokens");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"format": "bonikpos-whisper-onnx/1"}""")]
    [InlineData("""{"format": "something-else/2"}""")]
    public async Task BrokenConfig_MakesTheRecognizerUnavailable(string config)
    {
        var files = Files(_dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(files["config"])!);
        File.WriteAllText(files["config"], config);
        var provider = new FakeModelProvider().With(ModelRole.SpeechRecognition,
            FakeModelProvider.Model("whisper-broken", ModelRole.SpeechRecognition, "bonikpos-whisper-onnx/1", files));

        using var runtime = await InitializeAsync(provider);

        await AssertRecognizerUnavailableAsync(runtime, "failed to load");
    }

    [Fact]
    public async Task UnknownRecognizerFormat_IsReportedAsIncompatible()
    {
        var provider = new FakeModelProvider().With(ModelRole.SpeechRecognition,
            FakeModelProvider.Model("vosk", ModelRole.SpeechRecognition, "vosk/1", Files(_dir.Path)));

        using var runtime = await InitializeAsync(provider);

        await AssertRecognizerUnavailableAsync(runtime, "vosk/1");
        Assert.Equal(ModelState.IncompatibleContract, Assert.Single(provider.Failures).State);
    }
}
