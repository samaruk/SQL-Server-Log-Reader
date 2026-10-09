using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.AI.Recognition.Whisper;
using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

namespace BonikPOS.VoiceSearch.IntegrationTests.Whisper;

/// <summary>
/// Installs tests/fixtures/whisper-test-tiny (a RANDOM-WEIGHT Whisper exported by tools/models/export_whisper_onnx.py)
/// in a temporary model folder, exactly as a real model would be side-loaded, and loads it with the production runtime.
/// </summary>
public sealed class WhisperFixtureInstall : IAsyncLifetime
{
    public const string Prefix = "whisper-test-tiny";
    private readonly TempDirectory _dir = new();

    public WhisperFixtureInstall()
    {
        Directory.CreateDirectory(_dir.File("asr"));
        foreach (var suffix in new[] { "config.json", "encoder.onnx", "decoder.onnx", "tokens.txt" })
        {
            File.Copy(SolutionPaths.WhisperFixture($"{Prefix}-{suffix}"), _dir.File($"asr/{Prefix}-{suffix}"));
        }

        // Checksums pinned by the export (the config itself carries the encoder/decoder/tokens hashes).
        using var config = SolutionPaths.ReadJson(SolutionPaths.WhisperFixture($"{Prefix}-config.json"));
        var pinned = config.RootElement.GetProperty("sha256");
        string Sha(string suffix) => pinned.GetProperty($"{Prefix}-{suffix}").GetString()!;
        File.WriteAllText(_dir.File("models.json"), $$$"""
            {"format": "bonikpos-models/1", "models": [{"id": "whisper-random-test-tiny", "version": "1", "role": "SpeechRecognition",
              "format": "bonikpos-whisper-onnx/1", "isTestFixture": true, "license": "n/a (random weights)",
              "files": {"config": "asr/{{{Prefix}}}-config.json", "encoder": "asr/{{{Prefix}}}-encoder.onnx",
                        "decoder": "asr/{{{Prefix}}}-decoder.onnx", "tokens": "asr/{{{Prefix}}}-tokens.txt"},
              "sha256": {"asr/{{{Prefix}}}-encoder.onnx": "{{{Sha("encoder.onnx")}}}", "asr/{{{Prefix}}}-decoder.onnx": "{{{Sha("decoder.onnx")}}}",
                         "asr/{{{Prefix}}}-tokens.txt": "{{{Sha("tokens.txt")}}}"}}]}
            """);
        Provider = new FileSystemModelProvider([_dir.Path], checksumCachePath: null);
        Runtime = new OnnxVoiceModelRuntime(Provider, new PerformanceTelemetry(), allowTestFixtures: true);
        using var reference = SolutionPaths.ReadJson(SolutionPaths.WhisperFixture($"{Prefix}-reference.json"));
        Reference = reference.RootElement.Clone();
    }

    public FileSystemModelProvider Provider { get; }

    public OnnxVoiceModelRuntime Runtime { get; }

    public JsonElement Reference { get; }

    public WhisperOnnxRecognizer Recognizer => Assert.IsType<WhisperOnnxRecognizer>(Runtime.Recognizer);

    public async ValueTask InitializeAsync() => await Runtime.InitializeAsync(new VoiceSearchSettings(), CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        Runtime.Dispose();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The fixture has random weights: its "transcripts" are meaningless. It proves the tensor plumbing (mel front-end,
/// encoder, KV-cached decoder) and the decoding rules against the values export_whisper_onnx.py recorded with
/// onnxruntime in Python (greedy tokens) and the full-sequence PyTorch decoder (teacher forcing).
/// </summary>
public sealed class WhisperFixtureTests(WhisperFixtureInstall whisper, ITestOutputHelper output) : IClassFixture<WhisperFixtureInstall>
{
    private JsonElement Reference => whisper.Reference;

    private float[] Wav => SolutionPaths.ReadClip(Reference.GetProperty("wav").GetString()!);

    [Fact]
    public async Task RandomWeightFixture_IsRefusedWithoutTheTestOnlyFlag()
    {
        using var runtime = new OnnxVoiceModelRuntime(whisper.Provider, new PerformanceTelemetry());
        await runtime.InitializeAsync(new VoiceSearchSettings(), CancellationToken.None);

        Assert.False(runtime.Recognizer.Info.IsAvailable);
        Assert.Contains("test fixture", runtime.Recognizer.Info.StatusMessage);
        await Assert.ThrowsAsync<SpeechRecognitionUnavailableException>(() =>
            runtime.Recognizer.RecognizeAsync(Wav, new SpeechRecognitionOptions(), CancellationToken.None));
    }

    [Fact]
    public void LoadedFixture_IsLabelledAsARandomWeightTestModel()
    {
        var info = whisper.Runtime.Recognizer.Info;

        Assert.True(info.IsAvailable);
        Assert.Equal("whisper-random-test-tiny", info.ModelId);
        Assert.Equal("RANDOM-WEIGHT TEST MODEL - output is meaningless", info.StatusMessage);
        Assert.Equal(300 * 160, info.MaxAudioSamples);
        Assert.True(whisper.Runtime.Status.SpeechRecognition.Available);
    }

    [Fact]
    public void LogMelFeatures_MatchWhisperLogMelSpectrogram()
    {
        var shape = Reference.GetProperty("mel_shape").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal([1, 80, 300], shape);
        var mel = new float[80 * 300];

        new WhisperLogMel(80).Compute(Wav, 300, mel);

        var first = Reference.GetProperty("mel_first_frame").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var frame100 = Reference.GetProperty("mel_frame_100").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        float[] Column(int t) => [.. Enumerable.Range(0, 80).Select(m => mel[m * 300 + t])];
        double errFirst = Errors.MaxAbs(Column(0), first);
        double errFrame100 = Errors.MaxAbs(Column(100), frame100);
        double errMax = Math.Abs(mel.Max() - Reference.GetProperty("mel_max").GetDouble());
        double errMin = Math.Abs(mel.Min() - Reference.GetProperty("mel_min").GetDouble());
        double errMean = Math.Abs(mel.Average(v => (double)v) - Reference.GetProperty("mel_mean").GetDouble());
        output.WriteLine($"Whisper log-mel vs whisper.audio.log_mel_spectrogram: frame 0 {errFirst:E3}, frame 100 {errFrame100:E3}, " +
                         $"max {errMax:E3}, min {errMin:E3}, mean {errMean:E3}");
        // Normalised log10 scale (values about -0.6 .. 1.4); torch.stft ran in float32. Measured: frames 3.6e-7,
        // range 1.2e-7, mean 2.9e-8.
        Assert.True(Math.Max(errFirst, errFrame100) <= 5e-6, "frame error");
        Assert.True(Math.Max(errMax, errMin) <= 2e-6, "range error");
        Assert.True(errMean <= 1e-6, "mean error");
    }

    [Fact]
    public void GreedyDecoding_ReproducesThePythonTokensAndProbabilities()
    {
        var expectedTokens = Reference.GetProperty("expected_tokens").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var expectedLogProbs = Reference.GetProperty("expected_logprobs").EnumerateArray().Select(e => e.GetDouble()).ToList();
        int maxTokens = Reference.GetProperty("max_tokens").GetInt32();
        Assert.Equal("bn", Reference.GetProperty("language").GetString());

        // The reference decoded with the language forced to Bangla and without the app's repetition guard.
        var forced = whisper.Recognizer.Decode(Wav, RecognitionLanguageMode.Bangla, maxTokens, repetitionGuard: false, CancellationToken.None);

        Assert.Equal(expectedTokens, forced.Tokens);
        double logProbError = Errors.MaxAbs(forced.LogProbabilities, expectedLogProbs);
        double expectedNoSpeech = Reference.GetProperty("no_speech_prob").GetDouble();
        double noSpeechError = Math.Abs(forced.NoSpeechProbability - expectedNoSpeech) / expectedNoSpeech; // relative: p ~ 1e-5
        var languageLogits = Reference.GetProperty("language_logits");
        double languageError = Math.Max(
            Math.Abs(forced.LanguageLogits["bn"] - languageLogits.GetProperty("bn").GetDouble()),
            Math.Abs(forced.LanguageLogits["en"] - languageLogits.GetProperty("en").GetDouble()));
        output.WriteLine($"Greedy decoding: {forced.Tokens.Count} tokens identical; max |log-prob error| {logProbError:E3}; " +
                         $"no-speech relative error {noSpeechError:E3}; language-logit error {languageError:E3}");
        // Same ONNX graphs as the Python run; measured 2.2e-6 (log-probs) and 2.6e-6 (logits).
        Assert.True(logProbError <= 5e-5, "log-prob error");
        Assert.True(noSpeechError <= 1e-4, "no-speech error");
        Assert.True(languageError <= 5e-5, "language-logit error");
        Assert.Equal("bn", forced.Language);
        Assert.True(forced.Truncated); // stopped at max_tokens without an end-of-text token

        // Automatic language choice restricted to Bangla/English picks Bangla here (bn logit > en logit).
        var auto = whisper.Recognizer.Decode(Wav, RecognitionLanguageMode.AutoBanglaEnglish, 4, repetitionGuard: false, CancellationToken.None);
        Assert.Equal("bn", auto.Language);
        Assert.Equal(expectedTokens.Take(4), auto.Tokens);
    }

    [Fact]
    public void KvCachedDecoder_MatchesTheFullSequencePyTorchDecoder()
    {
        var sequence = Reference.GetProperty("teacher_forcing_sequence").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var positions = Reference.GetProperty("teacher_forcing_reference").EnumerateArray().ToList();

        var steps = whisper.Recognizer.TeacherForce(Wav, sequence);

        Assert.Equal(positions.Count, steps.Count);
        double worst = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            var expectedTop5 = positions[i].GetProperty("top5").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var expectedLogits = positions[i].GetProperty("top5_logits").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            Assert.Equal(expectedTop5, steps[i].Top5); // smallest gap between neighbouring top-5 logits is 0.028
            worst = Math.Max(worst, Errors.MaxAbs(steps[i].Top5Logits, expectedLogits));
        }

        output.WriteLine($"Teacher forcing over {steps.Count} positions: top-5 ids identical, max |logit error| {worst:E3}");
        // KV-cached ONNX decoder vs full-sequence PyTorch: the export validated 5.2e-6; measured 7.6e-6 here.
        Assert.True(worst <= 1e-4, $"logit error {worst:E3}");
    }

    [Fact]
    public async Task RecognizeAsync_AppliesTheRepetitionGuard_AndReportsTheDecodingStatistics()
    {
        var expectedLogProbs = Reference.GetProperty("expected_logprobs").EnumerateArray().Select(e => e.GetDouble()).ToList();

        var result = await whisper.Runtime.Recognizer.RecognizeAsync(Wav, new SpeechRecognitionOptions(), CancellationToken.None);

        // The random model repeats one token; decoding stops after 8 identical tokens instead of running to max tokens.
        Assert.Equal(8, result.TokenCount);
        Assert.Equal("bn", result.Language);
        Assert.Equal("whisper-random-test-tiny", result.ModelId);
        Assert.Equal(Math.Exp(expectedLogProbs.Take(8).Average()), result.Confidence, 1e-5);
        Assert.Equal(TimeSpan.FromSeconds(Wav.Length / 16000.0), result.AudioDuration);
        Assert.False(result.Truncated);

        var longAudio = await whisper.Runtime.Recognizer.RecognizeAsync(new float[16000 * 4], new SpeechRecognitionOptions(), CancellationToken.None);
        Assert.True(longAudio.Truncated); // longer than the model's 3 s window
    }
}
