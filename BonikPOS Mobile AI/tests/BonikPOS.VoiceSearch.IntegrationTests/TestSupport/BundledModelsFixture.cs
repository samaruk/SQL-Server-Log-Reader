using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Models;

namespace BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

/// <summary>
/// The models bundled in models/ (Silero VAD 6.2.3, GE2E / Resemblyzer 0.1.4), resolved through the production
/// <see cref="FileSystemModelProvider"/> (SHA-256 verified) and loaded once per test class by the production runtime.
/// </summary>
public sealed class BundledModelsFixture : IAsyncLifetime
{
    public FileSystemModelProvider Provider { get; } = new([SolutionPaths.Models], checksumCachePath: null);

    public PerformanceTelemetry Telemetry { get; } = new();

    public OnnxVoiceModelRuntime Runtime { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Runtime = new OnnxVoiceModelRuntime(Provider, Telemetry);
        await Runtime.InitializeAsync(new VoiceSearchSettings(), CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        Runtime.Dispose();
        return ValueTask.CompletedTask;
    }
}
