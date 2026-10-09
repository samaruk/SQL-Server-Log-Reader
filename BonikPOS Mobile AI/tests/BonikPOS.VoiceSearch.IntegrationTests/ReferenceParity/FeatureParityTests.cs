using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

namespace BonikPOS.VoiceSearch.IntegrationTests.ReferenceParity;

/// <summary>
/// C# feature front-ends against the Python reference implementations recorded by
/// tools/models/make_reference_fixtures.py (tests/fixtures/reference/reference.json). The references were computed in
/// float32 (numpy 2 / librosa 1.0 / kaldi-native-fbank) and stored as float32, while the C# code computes in double, so
/// agreement is expected to the level of float32 rounding of the respective quantity; the tolerances below allow about
/// ten times the measured error and are reported in the test output.
/// </summary>
public sealed class FeatureParityTests(ITestOutputHelper output)
{
    private static JsonElement Reference(string section)
    {
        using var doc = SolutionPaths.ReadJson(SolutionPaths.ReferenceFile("reference.json"));
        return doc.RootElement.GetProperty(section).Clone();
    }

    private static int[] Shape(JsonElement section) => [.. section.GetProperty("shape").EnumerateArray().Select(e => e.GetInt32())];

    [Theory]
    [InlineData("librosa_mel_filters_40", 40)] // librosa.filters.mel(sr=16000, n_fft=400, n_mels=40): GE2E front-end
    [InlineData("whisper_mel_filters", 80)]    // whisper/assets/mel_filters.npz (80 mels)
    public void SlaneyMelFilterBank_MatchesLibrosaAndWhisper(string section, int mels)
    {
        var reference = Reference(section);
        var expected = SolutionPaths.ReadF32(SolutionPaths.ReferenceFile(reference.GetProperty("file").GetString()!));
        Assert.Equal([mels, 201], Shape(reference));

        var bank = MelFilterBank.Slaney(16000, 400, mels);

        double maxAbs = Errors.MaxAbs(bank.Weights, expected);
        double maxWeight = expected.Max();
        output.WriteLine($"{section}: max |C# - reference| = {maxAbs:E3} (largest weight {maxWeight:E3}, relative {maxAbs / maxWeight:E3})");
        // Both are float32 roundings of the same double formula: differences are at the float32 ulp level.
        Assert.True(maxAbs <= 1e-6 * maxWeight + 1e-9, $"max abs error {maxAbs:E3}");
        for (int m = 0; m < mels; m++)
        {
            for (int b = 0; b < 201; b++)
            {
                Assert.Equal(expected[m * 201 + b] == 0, bank.Weights[m * 201 + b] == 0); // identical filter support
            }
        }
    }

    [Fact]
    public void Ge2ePowerMelSpectrogram_MatchesLibrosaMelspectrogram()
    {
        var reference = Reference("ge2e_mel");
        var expected = SolutionPaths.ReadF32(SolutionPaths.ReferenceFile(reference.GetProperty("file").GetString()!));
        var wav = SolutionPaths.ReadClip(reference.GetProperty("wav").GetString()!);
        int[] shape = Shape(reference);

        var mel = new PowerMelSpectrogram(16000, 400, 160, 40, CenterPadMode.Constant).Compute(wav);

        Assert.Equal(shape[0] * shape[1], mel.Length); // 1 + len / hop frames, centered with zero padding
        double max = expected.Max();
        double maxAbs = Errors.MaxAbs(mel, expected);
        double maxRelativeSignificant = 0, maxDb = 0;
        for (int i = 0; i < mel.Length; i++)
        {
            if (expected[i] >= 1e-4 * max)
            {
                maxRelativeSignificant = Math.Max(maxRelativeSignificant, Math.Abs(mel[i] - expected[i]) / expected[i]);
            }

            if (expected[i] >= 1e-8 * max)
            {
                maxDb = Math.Max(maxDb, Math.Abs(10 * Math.Log10(Math.Max(mel[i], 1e-30) / expected[i])));
            }
        }

        output.WriteLine($"GE2E mel {shape[0]}x{shape[1]}: max |diff| = {maxAbs:E3} ({maxAbs / max:E3} of the peak {max:E3}); " +
                         $"max relative error for bins >= 1e-4 x peak = {maxRelativeSignificant:E3}; max dB error for bins >= -80 dB = {maxDb:E3}");
        // float32 rounding of the reference: measured 1.7e-7 of the peak, 3.8e-7 relative, 1.9e-6 dB.
        Assert.True(maxAbs <= 2e-6 * max, $"max abs error {maxAbs:E3}");
        Assert.True(maxRelativeSignificant <= 5e-6, $"relative error {maxRelativeSignificant:E3}");
        Assert.True(maxDb <= 1e-4, $"dB error {maxDb:E3}");
    }

    [Fact]
    public void KaldiFbank_MatchesKaldiNativeFbank()
    {
        var reference = Reference("kaldi_fbank");
        var options = reference.GetProperty("options");
        Assert.Equal("povey", options.GetProperty("window_type").GetString());
        Assert.True(options.GetProperty("snip_edges").GetBoolean());
        Assert.True(options.GetProperty("use_power").GetBoolean());
        Assert.False(options.GetProperty("use_energy").GetBoolean());
        var expected = SolutionPaths.ReadF32(SolutionPaths.ReferenceFile(reference.GetProperty("file").GetString()!));
        var wav = SolutionPaths.ReadClip(reference.GetProperty("wav").GetString()!);
        int[] shape = Shape(reference);

        var fbank = new KaldiFbank(new KaldiFbankOptions
        {
            SampleRate = options.GetProperty("samp_freq").GetInt32(),
            FrameLengthMs = options.GetProperty("frame_length_ms").GetDouble(),
            FrameShiftMs = options.GetProperty("frame_shift_ms").GetDouble(),
            PreemphCoeff = options.GetProperty("preemph_coeff").GetDouble(),
            RemoveDcOffset = options.GetProperty("remove_dc_offset").GetBoolean(),
            Window = KaldiWindowType.Povey,
            NumBins = options.GetProperty("num_bins").GetInt32(),
            LowFreq = options.GetProperty("low_freq").GetDouble(),
            HighFreq = options.GetProperty("high_freq").GetDouble(),
            InputScale = (float)reference.GetProperty("input_scale").GetDouble(),
        });
        var feats = fbank.Compute(wav);

        Assert.Equal(shape[0] * shape[1], feats.Length);
        var errors = Enumerable.Range(0, feats.Length).Select(i => Math.Abs((double)feats[i] - expected[i])).Order().ToArray();
        double median = errors[errors.Length / 2], p99 = errors[(int)(errors.Length * 0.99)], max = errors[^1];

        output.WriteLine($"Kaldi fbank {shape[0]}x{shape[1]} (log mel energies {expected.Min():F1} .. {expected.Max():F1}): " +
                         $"|diff| median {median:E2}, 99th percentile {p99:E2}, max {max:E2}");
        // kaldi-native-fbank runs the FFT in float32. Bins far below the frame's peak (narrow low-frequency mel filters
        // about 20 log units down) carry its float32 rounding: measured median 3.8e-6, p99 5e-5, max 8.8e-4.
        Assert.True(median <= 5e-5, $"median error {median:E2}");
        Assert.True(p99 <= 5e-4, $"99th percentile error {p99:E2}");
        Assert.True(max <= 5e-3, $"max error {max:E2}");
    }
}
