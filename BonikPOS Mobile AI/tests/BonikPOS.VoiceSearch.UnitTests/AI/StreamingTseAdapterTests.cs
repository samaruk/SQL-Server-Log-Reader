using BonikPOS.VoiceSearch.AI.Extraction;
using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.AI;

/// <summary>
/// Manifest-driven validation of the streaming target-speaker-extraction adapter. A rejected model must leave the
/// runtime reporting extraction as unavailable, with a pass-through extractor that never claims to be active.
/// </summary>
public sealed class StreamingTseAdapterTests(GeneratedModels models) : IClassFixture<GeneratedModels>
{
    private const string ValidOptions =
        """{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out"}]}""";

    private static SpeakerEmbedding Embedding(string encoder = "enc-a", int dim = 256) =>
        new(encoder, "1", [.. Enumerable.Range(0, dim).Select(i => (float)Math.Sin(i + 1))]);

    private async Task<(OnnxVoiceModelRuntime Runtime, FakeModelProvider Provider)> LoadAsync(
        string optionsJson, string? modelPath = null, ResolvedModel? encoder = null)
    {
        var provider = new FakeModelProvider().With(ModelRole.TargetSpeakerExtraction,
            FakeModelProvider.Model("tse-test", ModelRole.TargetSpeakerExtraction, "streaming-tse-onnx/1",
                new Dictionary<string, string> { ["model"] = modelPath ?? models.Tse }, optionsJson));
        if (encoder is not null)
        {
            provider.With(ModelRole.SpeakerEncoder, encoder);
        }

        var runtime = new OnnxVoiceModelRuntime(provider, new PerformanceTelemetry());
        await runtime.InitializeAsync(new VoiceSearchSettings(), TestContext.Current.CancellationToken);
        return (runtime, provider);
    }

    private static void AssertUnavailable(OnnxVoiceModelRuntime runtime, string expectedInMessage)
    {
        var status = runtime.Status.TargetSpeakerExtraction;
        Assert.False(status.Available, status.Description);
        Assert.Contains(expectedInMessage, status.Description);
        Assert.Null(runtime.ExtractionRequiredEncoderId);
        Assert.Equal(TargetSpeakerExtractionAvailability.IncompatibleModel, runtime.CreateTargetSpeakerExtractorStatusPreview().Availability);

        // The fallback copies audio unchanged and never reports itself as active, whatever target it is given.
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(Embedding());
        Assert.False(extractor.Status.IsActive);
        var input = Signals.Noise(640, seed: 4);
        var output = new float[640];
        extractor.Process(input, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public async Task ValidManifest_Loads_AndActivatesOnlyWithAMatchingOwnerEmbedding()
    {
        var (runtime, provider) = await LoadAsync(ValidOptions);
        using var _ = runtime;

        Assert.True(runtime.Status.TargetSpeakerExtraction.Available, runtime.Status.TargetSpeakerExtraction.Description);
        Assert.Empty(provider.Failures);
        Assert.Equal("enc-a", runtime.ExtractionRequiredEncoderId);
        Assert.Equal(TargetSpeakerExtractionAvailability.NeedsReEnrollment, runtime.CreateTargetSpeakerExtractorStatusPreview().Availability);

        using var extractor = runtime.CreateTargetSpeakerExtractor();
        Assert.IsType<OnnxStreamingTargetSpeakerExtractor>(extractor);
        Assert.Equal(TargetSpeakerExtractionAvailability.NeedsReEnrollment, extractor.Status.Availability);

        extractor.SetTarget(Embedding("enc-other"));                 // encoder-id mismatch
        Assert.Equal(TargetSpeakerExtractionAvailability.NeedsReEnrollment, extractor.Status.Availability);
        Assert.Contains("enc-a", extractor.Status.Message);
        Assert.Contains("enc-other", extractor.Status.Message);

        extractor.SetTarget(Embedding(dim: 128));                     // wrong embedding dimension
        Assert.Equal(TargetSpeakerExtractionAvailability.NeedsReEnrollment, extractor.Status.Availability);
        Assert.Contains("256-dim", extractor.Status.Message);

        extractor.SetTarget(Embedding());
        Assert.True(extractor.Status.IsActive);
        Assert.Equal(320, extractor.Status.ChunkSamples);

        extractor.SetTarget(null);
        Assert.False(extractor.Status.IsActive);
    }

    [Theory]
    [InlineData(320, 0)]   // pipeline frames that are whole chunks: no added delay
    [InlineData(100, 319)] // frames that do not divide the chunk: a fixed alignment delay of chunk - 1 samples
    [InlineData(512, 319)]
    public async Task ActiveExtractor_StreamsChunkByChunk_WithAFixedDelay(int blockSize, int expectedDelay)
    {
        var (runtime, _) = await LoadAsync(ValidOptions);
        using var __ = runtime;
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(Embedding());
        var input = Signals.Noise(320 * 20, seed: 7);
        var output = new float[input.Length];

        for (int offset = 0; offset + blockSize <= input.Length; offset += blockSize)
        {
            extractor.Process(input.AsSpan(offset, blockSize), output.AsSpan(offset, blockSize));
        }

        int produced = input.Length / blockSize * blockSize;
        Assert.Equal(expectedDelay, extractor.Status.AlgorithmicLatencySamples);
        Assert.All(output.Take(expectedDelay), s => Assert.Equal(0f, s));
        // This test graph returns its input unchanged, so the stream must be exactly the input, delayed.
        Assert.Equal(input.Take(produced - expectedDelay), output.Skip(expectedDelay).Take(produced - expectedDelay));
    }

    [Theory]
    [InlineData("""{"embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out"}]}""", "chunk_samples")]
    [InlineData("""{"chunk_samples": 320, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out"}]}""", "embedding_dim")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "states": [{"input": "state_in", "output": "state_out"}]}""", "required_encoder_id")]
    [InlineData("""{"chunk_samples": "320", "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out"}]}""", "chunk_samples")]
    public async Task MissingOrMistypedRequiredKeys_LeaveExtractionUnavailable(string options, string key)
    {
        var (runtime, provider) = await LoadAsync(options);
        using var _ = runtime;

        AssertUnavailable(runtime, key);
        var failure = Assert.Single(provider.Failures);
        Assert.Equal((ModelRole.TargetSpeakerExtraction, ModelState.IncompatibleContract), (failure.Role, failure.State));
    }

    [Theory]
    [InlineData("""{"chunk_samples": 160, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out"}]}""", "chunk_samples 160")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 128, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out"}]}""", "must be 128 but is 256")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "sample_rate": 8000, "states": [{"input": "state_in", "output": "state_out"}]}""", "16 kHz")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a"}""", "unexpected inputs: state_in")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "h_in", "output": "state_out"}]}""", "h_in")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out", "shape": [1, 16]}]}""", "does not match")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "audio_input": "mix", "states": [{"input": "state_in", "output": "state_out"}]}""", "mix")]
    public async Task ManifestsThatContradictTheGraph_LeaveExtractionUnavailable(string options, string expectedInMessage)
    {
        var (runtime, _) = await LoadAsync(options);
        using var __ = runtime;

        AssertUnavailable(runtime, expectedInMessage);
    }

    [Theory]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"output": "state_out"}]}""", "input")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in"}]}""", "output")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": ["state_in"]}""", "state")]
    [InlineData("""{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out", "shape": ["1", "8"]}]}""", "shape")]
    public async Task MalformedStateDeclarations_LeaveExtractionUnavailable(string options, string expectedInMessage)
    {
        var (runtime, _) = await LoadAsync(options);
        using var __ = runtime;

        AssertUnavailable(runtime, expectedInMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-320)]
    public async Task NonPositiveChunkSize_IsRejectedEvenWhenTheGraphWidthIsSymbolic(int chunk)
    {
        string options = $$"""{"chunk_samples": {{chunk}}, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out", "shape": [1, 8]}]}""";
        var (runtime, _) = await LoadAsync(options, models.TseDynamic);
        using var _ = runtime;

        AssertUnavailable(runtime, "chunk_samples");
    }

    [Fact]
    public async Task SymbolicStateShape_MustBeDeclaredInTheManifest()
    {
        var (runtime, _) = await LoadAsync(ValidOptions, models.TseDynamic);
        using (runtime)
        {
            AssertUnavailable(runtime, "declare its shape");
        }

        var (declared, _) = await LoadAsync(
            """{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "enc-a", "states": [{"input": "state_in", "output": "state_out", "shape": [1, 8]}]}""",
            models.TseDynamic);
        using (declared)
        {
            Assert.True(declared.Status.TargetSpeakerExtraction.Available, declared.Status.TargetSpeakerExtraction.Description);
        }
    }

    [Fact]
    public async Task ExtractionModelConditionedOnAnotherEncoder_IsRejectedWhenTheInstalledEncoderDiffers()
    {
        var encoder = FakeModelProvider.Model("enc-installed", ModelRole.SpeakerEncoder, "fbank-speaker-onnx",
            new Dictionary<string, string> { ["model"] = models.FbankEncoder }, """{"embedding_dim": 80}""");

        var (runtime, provider) = await LoadAsync(ValidOptions, encoder: encoder);
        using var _ = runtime;

        Assert.True(runtime.Status.SpeakerEncoder.Available, runtime.Status.SpeakerEncoder.Description);
        AssertUnavailable(runtime, "enc-a");
        Assert.Contains("enc-installed", runtime.Status.TargetSpeakerExtraction.Description);
        Assert.Contains(provider.Failures, f => f.Role == ModelRole.TargetSpeakerExtraction);
    }

    [Fact]
    public async Task CorruptModelFile_LeavesExtractionUnavailable()
    {
        var (runtime, provider) = await LoadAsync(ValidOptions, models.NotAModel);
        using var _ = runtime;

        Assert.False(runtime.Status.TargetSpeakerExtraction.Available);
        Assert.Equal(ModelState.LoadFailed, Assert.Single(provider.Failures).State);
        Assert.False(runtime.CreateTargetSpeakerExtractor().Status.IsActive);
    }
}
