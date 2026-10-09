using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Extraction;
using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

namespace BonikPOS.VoiceSearch.IntegrationTests.ReferenceParity;

/// <summary>
/// tests/fixtures/tse/pgtcrn-c16-e256-random.onnx is a pGTCRN graph with RANDOM, UNTRAINED weights (it extracts no
/// speaker). Python streamed a clip through it with a reference STFT / inverse STFT (tools/models/tse/export_pgtcrn_onnx.py);
/// the C# adapter (streaming-tse-stft-onnx/1) must reproduce that output for any pipeline frame size.
/// </summary>
public sealed class PgtcrnStftAdapterTests(ITestOutputHelper output) : IDisposable
{
    private const string ModelFile = "pgtcrn-c16-e256-random.onnx";
    private const int ReferenceLatency = 256; // n_fft - hop of the reference
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Fixture(string name) => Path.Combine(SolutionPaths.Fixtures, "tse", name);

    private async Task<OnnxVoiceModelRuntime> RuntimeAsync(bool flaggedAsTestFixture = true, bool allowTestFixtures = true)
    {
        Directory.CreateDirectory(_dir.File("tse"));
        File.Copy(Fixture(ModelFile), _dir.File($"tse/{ModelFile}"), overwrite: true);
        using var readme = SolutionPaths.ReadJson(Fixture("README.json"));
        string sha = readme.RootElement.GetProperty("model").GetProperty("sha256").GetString()!;
        File.WriteAllText(_dir.File("models.json"), $$$"""
            {"format": "bonikpos-models/1", "models": [{"id": "pgtcrn-random-fixture", "version": "1", "role": "TargetSpeakerExtraction",
              "format": "streaming-tse-stft-onnx/1", "files": {"model": "tse/{{{ModelFile}}}"}, "isTestFixture": {{{(flaggedAsTestFixture ? "true" : "false")}}},
              "sha256": {"tse/{{{ModelFile}}}": "{{{sha}}}"},
              "options": {"embedding_dim": 256, "required_encoder_id": "ge2e-resemblyzer"}}]}
            """);
        // The fixture first, then the bundled models (the GE2E encoder the fixture is declared for).
        var provider = new FileSystemModelProvider([_dir.Path, SolutionPaths.Models], checksumCachePath: null);
        var runtime = new OnnxVoiceModelRuntime(provider, new PerformanceTelemetry(), allowTestFixtures: allowTestFixtures);
        await runtime.InitializeAsync(new VoiceSearchSettings(), CancellationToken.None);
        return runtime;
    }

    [Theory]
    [InlineData(256)] // one hop per call
    [InlineData(320)] // the pipeline's 20 ms frames
    [InlineData(160)] // 10 ms frames
    public async Task Adapter_ReproducesThePythonStreamingReference(int frame)
    {
        using var runtime = await RuntimeAsync();
        Assert.True(runtime.Status.TargetSpeakerExtraction.Available, runtime.Status.TargetSpeakerExtraction.Description);
        var input = SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-input.f32"));
        var embedding = SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-embedding.f32"));
        var expected = SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-expected.f32"));
        Assert.Equal(32000, input.Length);
        Assert.Equal(256, embedding.Length);

        using var extractor = runtime.CreateTargetSpeakerExtractor();
        Assert.IsType<StftStreamingTargetSpeakerExtractor>(extractor);
        extractor.SetTarget(new SpeakerEmbedding("ge2e-resemblyzer", "0.1.4", embedding));
        Assert.True(extractor.Status.IsActive);

        int usable = input.Length / frame * frame;
        var result = new float[usable];
        for (int offset = 0; offset < usable; offset += frame)
        {
            extractor.Process(input.AsSpan(offset, frame), result.AsSpan(offset, frame));
        }

        // The adapter adds an alignment delay (< hop) when the frame size is not a multiple of the hop.
        int latency = extractor.Status.AlgorithmicLatencySamples;
        int alignment = latency - ReferenceLatency;
        Assert.InRange(alignment, 0, 255);
        double worst = 0;
        for (int i = 0; i < usable; i++)
        {
            double e = i < alignment ? 0 : expected[i - alignment];
            worst = Math.Max(worst, Math.Abs(result[i] - e));
        }

        double peak = expected.Max(v => Math.Abs(v));
        output.WriteLine($"pGTCRN STFT adapter, {frame}-sample frames: latency {latency} samples, max |C# - Python| = {worst:E3} (signal peak {peak:F3})");
        Assert.True(worst <= 1e-5, $"max error {worst:E3}"); // measured 3.3e-7 when the fixture was made
    }

    [Fact]
    public async Task Adapter_UsesTheSmallestConstantDelay_AndStaysContinuousWhenTheBlockSizeChanges()
    {
        using var runtime = await RuntimeAsync();
        var input = SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-input.f32"));
        var expected = SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-expected.f32"));
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(new SpeakerEmbedding("ge2e-resemblyzer", "0.1.4", SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-embedding.f32"))));

        // 20 ms blocks first (alignment 256 - gcd(320, 256) = 192), then 10 ms and odd-sized blocks.
        int[] sizes = [320, 320, 320, 160, 160, 333, 320, 100, 256, 320];
        var result = new float[sizes.Sum()];
        int offset = 0;
        var latencies = new List<int>();
        foreach (int size in sizes)
        {
            extractor.Process(input.AsSpan(offset, size), result.AsSpan(offset, size));
            offset += size;
            latencies.Add(extractor.Status.AlgorithmicLatencySamples);
        }

        Assert.Equal(ReferenceLatency + 192, latencies[0]);
        Assert.Equal(latencies.Order().ToList(), latencies); // the delay only ever grows, never jumps back

        // Every sample still lines up with the reference at the final delay once the stream is past the last growth.
        int alignment = latencies[^1] - ReferenceLatency;
        int settled = sizes.Take(sizes.Length - 1).Sum();
        double worst = 0;
        for (int i = settled; i < result.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(result[i] - expected[i - alignment]));
        }

        output.WriteLine($"Variable blocks: latency {string.Join(", ", latencies)} samples; last block max |error| = {worst:E3}");
        Assert.True(worst <= 1e-5, $"max error {worst:E3}");
    }

    [Fact]
    public async Task Adapter_PassesAudioThroughUnchanged_WithoutACompatibleTarget()
    {
        using var runtime = await RuntimeAsync();
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(new SpeakerEmbedding("wespeaker-resnet34", "1", Enumerable.Repeat(1f, 256).ToArray()));
        var input = SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-input.f32")).AsSpan(0, 3200).ToArray();
        var passThrough = new float[input.Length];

        extractor.Process(input, passThrough);

        Assert.False(extractor.Status.IsActive);
        Assert.Equal(input, passThrough); // never presented as "extracted"
    }

    [Fact]
    public async Task UntrainedGraph_IsRefusedByTheApp_EvenWhenTheManifestDoesNotFlagIt()
    {
        // The export records "bonik.weights" = "RANDOM-UNTRAINED ..." in the ONNX metadata; the app must not show it as
        // working extraction just because a manifest forgot "isTestFixture".
        using var runtime = await RuntimeAsync(flaggedAsTestFixture: false, allowTestFixtures: false);

        Assert.False(runtime.Status.TargetSpeakerExtraction.Available);
        Assert.Contains("untrained weights", runtime.Status.TargetSpeakerExtraction.Description, StringComparison.Ordinal);
        Assert.True(runtime.Status.SpeakerEncoder.Available); // the other models still load
        using var extractor = runtime.CreateTargetSpeakerExtractor();
        extractor.SetTarget(new SpeakerEmbedding("ge2e-resemblyzer", "0.1.4", SolutionPaths.ReadF32(Fixture("pgtcrn-fixture-embedding.f32"))));
        Assert.False(extractor.Status.IsActive);
    }

    [Fact]
    public void Fixture_IsDeclaredAsRandomWeightsInItsReadme()
    {
        using var readme = SolutionPaths.ReadJson(Fixture("README.json"));
        Assert.Contains("RANDOM, UNTRAINED WEIGHTS", readme.RootElement.GetProperty("notice").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, readme.RootElement.GetProperty("reference").ValueKind);
    }
}
