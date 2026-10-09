using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Extraction;
using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;
using Microsoft.ML.OnnxRuntime;

namespace BonikPOS.VoiceSearch.IntegrationTests.ReferenceParity;

/// <summary>
/// tests/fixtures/reference/tse-plumbing-fixture.onnx is an ARITHMETIC test graph with the tensor layout of a streaming
/// target-speaker extractor (it is not a speech model): enhanced = mixture * sigmoid(sum(spk_emb)) + mean(state_in);
/// state_out = state_in shifted left by one with mean(mixture) appended. It verifies chunking, state carry-over and
/// embedding binding of the manifest-driven adapter.
/// </summary>
public sealed class TsePlumbingFixtureTests(ITestOutputHelper output) : IDisposable
{
    private const int Chunk = 320;
    private const int StateDim = 8;
    private readonly TempDirectory _dir = new();
    private static readonly JsonElement Reference = LoadReference();

    public void Dispose() => _dir.Dispose();

    private static JsonElement LoadReference()
    {
        using var doc = SolutionPaths.ReadJson(SolutionPaths.ReferenceFile("reference.json"));
        return doc.RootElement.GetProperty("tse_plumbing_fixture").Clone();
    }

    /// <summary>mixture[c][i] = i / chunk + c (float32 arithmetic, as numpy computed it).</summary>
    private static float[] Mixture(int chunks)
    {
        var x = new float[chunks * Chunk];
        for (int c = 0; c < chunks; c++)
        {
            for (int i = 0; i < Chunk; i++)
            {
                x[c * Chunk + i] = (float)i / Chunk + c;
            }
        }

        return x;
    }

    /// <summary>The graph's arithmetic, evaluated in double, for a given sum of the embedding.</summary>
    private static double[] ExpectedStream(float[] mixture, double embeddingSum)
    {
        double gain = 1 / (1 + Math.Exp(-embeddingSum));
        var state = new double[StateDim];
        var result = new double[mixture.Length];
        for (int c = 0; c < mixture.Length / Chunk; c++)
        {
            double stateMean = state.Average();
            double chunkMean = 0;
            for (int i = 0; i < Chunk; i++)
            {
                double x = mixture[c * Chunk + i];
                result[c * Chunk + i] = x * gain + stateMean;
                chunkMean += x;
            }

            Array.Copy(state, 1, state, 0, StateDim - 1);
            state[^1] = chunkMean / Chunk;
        }

        return result;
    }

    private async Task<OnnxVoiceModelRuntime> RuntimeAsync(bool allowTestFixtures, string requiredEncoder = "ge2e-resemblyzer")
    {
        Directory.CreateDirectory(_dir.File("tse"));
        File.Copy(SolutionPaths.ReferenceFile("tse-plumbing-fixture.onnx"), _dir.File("tse/tse-plumbing-fixture.onnx"), overwrite: true);
        string sha = Reference.GetProperty("sha256").GetString()!;
        File.WriteAllText(_dir.File("models.json"), $$$"""
            {"format": "bonikpos-models/1", "models": [{"id": "tse-plumbing-fixture", "version": "1", "role": "TargetSpeakerExtraction",
              "format": "streaming-tse-onnx/1", "files": {"model": "tse/tse-plumbing-fixture.onnx"}, "isTestFixture": true,
              "sha256": {"tse/tse-plumbing-fixture.onnx": "{{{sha}}}"},
              "options": {"chunk_samples": {{{Chunk}}}, "embedding_dim": 256, "required_encoder_id": "{{{requiredEncoder}}}",
                          "states": [{"input": "state_in", "output": "state_out"}]}}]}
            """);
        // The fixture comes first, then the bundled models (VAD + the GE2E encoder the fixture is declared for).
        var provider = new FileSystemModelProvider([_dir.Path, SolutionPaths.Models], checksumCachePath: null);
        var runtime = new OnnxVoiceModelRuntime(provider, new PerformanceTelemetry(), allowTestFixtures: allowTestFixtures);
        await runtime.InitializeAsync(new VoiceSearchSettings(), CancellationToken.None);
        return runtime;
    }

    private static SpeakerEmbedding EqualEmbedding(string encoder = "ge2e-resemblyzer") =>
        new(encoder, "0.1.4", Enumerable.Repeat(1f, 256).ToArray()); // unit length: every element 1/16, sum 16

    [Fact]
    public void Fixture_RunsInDotnetOnnxRuntimeExactlyAsRecordedInPython()
    {
        Assert.Equal(Chunk, Reference.GetProperty("chunk_samples").GetInt32());
        Assert.Equal(StateDim, Reference.GetProperty("state_dim").GetInt32());
        using var session = new InferenceSession(SolutionPaths.ReferenceFile("tse-plumbing-fixture.onnx"));
        using var runOptions = new RunOptions();
        var embedding = Enumerable.Repeat(1f / 256, 256).ToArray(); // the reference input: 1/256 everywhere
        var state = new float[StateDim];
        var mixture = Mixture(3);
        double worst = 0;
        foreach (var expected in Reference.GetProperty("expected").EnumerateArray())
        {
            int c = expected.GetProperty("chunk").GetInt32();
            using var mix = OrtValue.CreateTensorValueFromMemory(mixture.AsMemory(c * Chunk, Chunk).ToArray(), [1, Chunk]);
            using var emb = OrtValue.CreateTensorValueFromMemory(embedding, [1, 256]);
            using var st = OrtValue.CreateTensorValueFromMemory(state.ToArray(), [1, StateDim]);
            using var outputs = session.Run(runOptions, ["mixture", "spk_emb", "state_in"], [mix, emb, st], ["enhanced", "state_out"]);
            var enhanced = outputs[0].GetTensorDataAsSpan<float>();
            state = outputs[1].GetTensorDataAsSpan<float>().ToArray();

            worst = Math.Max(worst, Math.Abs(enhanced[0] - expected.GetProperty("enhanced_first").GetDouble()));
            worst = Math.Max(worst, Math.Abs(enhanced[^1] - expected.GetProperty("enhanced_last").GetDouble()));
            var expectedState = expected.GetProperty("state_out").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            worst = Math.Max(worst, Errors.MaxAbs(state, expectedState));
        }

        output.WriteLine($"TSE fixture, 3 chunks: max |C# ORT - Python ORT| = {worst:E3}");
        Assert.True(worst <= 1e-6, $"max error {worst:E3}");
    }

    [Fact]
    public async Task TestFixture_IsRefusedByTheRuntime_UnlessTestFixturesAreAllowed()
    {
        using var runtime = await RuntimeAsync(allowTestFixtures: false);

        Assert.True(runtime.Status.SpeakerEncoder.Available);      // the real bundled encoder still loads
        Assert.False(runtime.Status.TargetSpeakerExtraction.Available);
        Assert.Contains("test fixture", runtime.Status.TargetSpeakerExtraction.Description);
        Assert.Null(runtime.ExtractionRequiredEncoderId);
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(EqualEmbedding());
        Assert.False(extractor.Status.IsActive);
    }

    [Theory]
    [InlineData(Chunk, 0)]       // pipeline frames that equal the chunk: no added delay
    [InlineData(160, Chunk - 1)] // smaller frames: output delayed by chunk - 1 samples so every call returns full output
    public async Task Adapter_StreamsChunks_CarriesStateAndBindsTheOwnerEmbedding(int frame, int delay)
    {
        using var runtime = await RuntimeAsync(allowTestFixtures: true);
        Assert.True(runtime.Status.TargetSpeakerExtraction.Available, runtime.Status.TargetSpeakerExtraction.Description);
        Assert.Equal("ge2e-resemblyzer", runtime.ExtractionRequiredEncoderId);
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        Assert.IsType<OnnxStreamingTargetSpeakerExtractor>(extractor);
        extractor.SetTarget(EqualEmbedding());
        Assert.True(extractor.Status.IsActive);

        var mixture = Mixture(12); // more chunks than state slots: the state window slides
        var outputStream = new float[mixture.Length];
        for (int offset = 0; offset < mixture.Length; offset += frame)
        {
            extractor.Process(mixture.AsSpan(offset, frame), outputStream.AsSpan(offset, frame));
        }

        var expected = ExpectedStream(mixture, embeddingSum: 16);
        double worst = 0;
        for (int i = 0; i < outputStream.Length; i++)
        {
            double e = i < delay ? 0 : expected[i - delay];
            worst = Math.Max(worst, Math.Abs(outputStream[i] - e));
        }

        output.WriteLine($"TSE adapter, {frame}-sample frames: delay {extractor.Status.AlgorithmicLatencySamples}, max |error| = {worst:E3}");
        Assert.Equal(delay, extractor.Status.AlgorithmicLatencySamples);
        Assert.True(worst <= 1e-5, $"max error {worst:E3}");

        // Reset clears the recurrent state: the next chunk sees an all-zero state again.
        extractor.Reset();
        var again = new float[frame];
        extractor.Process(mixture.AsSpan(0, frame), again);
        if (delay == 0)
        {
            for (int i = 0; i < frame; i++)
            {
                Assert.Equal(expected[i], again[i], 1e-5);
            }
        }
    }

    [Fact]
    public async Task Adapter_IsActiveWithARealGe2eOwnerEmbedding_AndRejectsEmbeddingsFromOtherEncoders()
    {
        using var runtime = await RuntimeAsync(allowTestFixtures: true);
        var owner = runtime.SpeakerEncoder!.EmbedUtterance(SolutionPaths.ReadClip("synthetic-voiceA-1.wav"), TestContext.Current.CancellationToken);
        using var extractor = runtime.CreateTargetSpeakerExtractor();

        extractor.SetTarget(new SpeakerEmbedding("wespeaker-resnet34", "1", Enumerable.Repeat(1f, 256).ToArray()));
        Assert.Equal(TargetSpeakerExtractionAvailability.NeedsReEnrollment, extractor.Status.Availability);
        var input = Mixture(2);
        var passThrough = new float[input.Length];
        extractor.Process(input, passThrough);
        Assert.Equal(input, passThrough); // not active: audio is copied unchanged, never "extracted"

        extractor.SetTarget(owner);
        Assert.True(extractor.Status.IsActive);
        var extracted = new float[input.Length];
        extractor.Process(input, extracted);
        double sum = owner.Vector.Sum(v => (double)v);
        var expected = ExpectedStream(input, sum);
        Assert.Equal(expected[^1], extracted[^1], 1e-5);
    }

    [Fact]
    public async Task ExtractionModelDeclaredForAnotherEncoder_IsUnavailable()
    {
        using var runtime = await RuntimeAsync(allowTestFixtures: true, requiredEncoder: "wespeaker-resnet34");

        var status = runtime.Status.TargetSpeakerExtraction;
        output.WriteLine(status.Description);
        Assert.False(status.Available);
        Assert.Contains("wespeaker-resnet34", status.Description);
        Assert.Contains("ge2e-resemblyzer", status.Description);
        Assert.Equal(TargetSpeakerExtractionAvailability.IncompatibleModel, runtime.CreateTargetSpeakerExtractorStatusPreview().Availability);
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(EqualEmbedding());
        Assert.False(extractor.Status.IsActive);
    }
}
