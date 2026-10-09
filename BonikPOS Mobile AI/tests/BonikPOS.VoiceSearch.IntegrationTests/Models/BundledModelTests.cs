using System.Runtime.InteropServices;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.AI.Speakers;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

namespace BonikPOS.VoiceSearch.IntegrationTests.Models;

/// <summary>The models bundled in models/ are verified against models.json and loaded by the production adapters.</summary>
public sealed class BundledModelTests(BundledModelsFixture models, ITestOutputHelper output) : IClassFixture<BundledModelsFixture>
{
    [Fact]
    public async Task BundledModels_PassTheirChecksums_AndOptionalRolesAreReportedAsNotInstalled()
    {
        var status = await models.Provider.GetStatusAsync(CancellationToken.None);
        foreach (var s in status)
        {
            output.WriteLine($"{s.Role}: {s.State} {s.ModelId} {s.Version} - {s.Message}");
        }

        var vad = status.Single(s => s.Role == ModelRole.VoiceActivity);
        Assert.Equal(ModelState.Ready, vad.State);
        Assert.Equal("Ready (checksums verified).", vad.Message);
        Assert.Equal("silero-vad", vad.ModelId);
        Assert.Equal("MIT", vad.License);
        Assert.All(vad.Files, f => Assert.Equal(f.ExpectedSha256, f.ActualSha256));

        var encoder = status.Single(s => s.Role == ModelRole.SpeakerEncoder);
        Assert.Equal(ModelState.Ready, encoder.State);
        Assert.Equal("ge2e-resemblyzer", encoder.ModelId);
        Assert.Equal("Apache-2.0", encoder.License);

        Assert.Equal(ModelState.NotInstalled, status.Single(s => s.Role == ModelRole.TargetSpeakerExtraction).State);
        Assert.Equal(ModelState.NotInstalled, status.Single(s => s.Role == ModelRole.SpeechRecognition).State);
    }

    [Fact]
    public void Runtime_LoadsVadAndSpeakerEncoder_WithTheManifestOptions_AndReportsTheRestAsUnavailable()
    {
        var runtime = models.Runtime;
        output.WriteLine($"VAD: {runtime.Status.VoiceActivity}");
        output.WriteLine($"Encoder: {runtime.Status.SpeakerEncoder}");

        Assert.True(runtime.Status.IsInitialized);
        Assert.True(runtime.Status.VoiceActivity.Available);
        Assert.Equal("CPU", runtime.Status.VoiceActivity.ExecutionProvider);
        Assert.Equal("silero-vad@6.2.3", runtime.CreateVoiceActivityDetector().ModelId);

        var encoder = Assert.IsType<Ge2eSpeakerEncoder>(runtime.SpeakerEncoder);
        var info = encoder.Info;
        Assert.Equal(("ge2e-resemblyzer", "0.1.4", 256, 16000), (info.ModelId, info.ModelVersion, info.EmbeddingDimension, info.SampleRate));
        Assert.Equal("f8e45a3d5d4aa7ea36c703594f618706bce7a68b6ad4e9b22f68b88a77c86937", info.ModelSha256);
        Assert.Equal(1.6, info.WindowSeconds, 1e-12);         // 160 frames x 10 ms
        Assert.Equal(0.8, info.MinimumSpeechSeconds, 1e-12);
        Assert.Equal(0.75, info.Calibration.DefaultAcceptThreshold);
        Assert.Equal(0.68, info.Calibration.MinAcceptThreshold);
        Assert.Equal(0.86, info.Calibration.MaxAcceptThreshold);
        Assert.Equal(0.7, info.Calibration.EnrollmentMinConsistency);
        Assert.Equal(0.65, info.Calibration.SingleSpeakerMinWindow);
        Assert.NotNull(runtime.OwnerVerifier);

        Assert.False(runtime.Status.TargetSpeakerExtraction.Available);
        Assert.Equal(TargetSpeakerExtractionAvailability.NotInstalled, runtime.CreateTargetSpeakerExtractorStatusPreview().Availability);
        Assert.False(runtime.CreateTargetSpeakerExtractor().Status.IsActive);
        Assert.False(runtime.Recognizer.Info.IsAvailable);
        Assert.Contains("not installed", runtime.Recognizer.Info.StatusMessage);
    }

    [Fact]
    public void SpeakerEncoder_EmbedsWindows_WithTheDocumentedLayout()
    {
        var encoder = models.Runtime.SpeakerEncoder!;
        var speech = SolutionPaths.ReadClip("synthetic-voiceA-1.wav");

        var windows = encoder.EmbedWindows(speech, encoder.Info.WindowSeconds, encoder.Info.WindowSeconds / 2, TestContext.Current.CancellationToken);

        // Full 1.6 s windows every 0.8 s; over 2.83 s that is windows at 0 and 0.8 s (the last 0.43 s is in no window).
        Assert.Equal([0, 12800], windows.Select(w => w.StartSample));
        Assert.All(windows, w => Assert.Equal(25600, w.LengthSamples));
        Assert.All(windows, w => Assert.Equal(256, w.Embedding.Dimension));
        Assert.All(windows, w => Assert.Equal(1.0, w.Embedding.Vector.Sum(v => (double)v * v), 1e-5));
        var whole = encoder.EmbedUtterance(speech, TestContext.Current.CancellationToken);
        Assert.All(windows, w => Assert.True(w.Embedding.CosineSimilarity(whole) > 0.5));
    }
}

public sealed class OnnxRuntimeTelemetryTests
{
    [DllImport("libc", EntryPoint = "getenv")]
    private static extern IntPtr NativeGetEnv(string name);

    [Fact]
    public void Disable_IsIdempotent_AndSetsTheOptOutForTheNativeRuntime()
    {
        OnnxRuntimeTelemetry.Disable();
        OnnxRuntimeTelemetry.Disable(); // second call is a no-op

        Assert.Equal("ORT_DISABLE_TELEMETRY", OnnxRuntimeTelemetry.EnvironmentVariable);
        Assert.Equal("1", Environment.GetEnvironmentVariable(OnnxRuntimeTelemetry.EnvironmentVariable));
        if (OperatingSystem.IsLinux())
        {
            Assert.True(OnnxRuntimeTelemetry.NativeFlagSet);
            // The value is visible in the C runtime environment that ONNX Runtime itself reads, not only in .NET's copy.
            Assert.Equal("1", Marshal.PtrToStringUTF8(NativeGetEnv(OnnxRuntimeTelemetry.EnvironmentVariable)));
        }
    }
}
