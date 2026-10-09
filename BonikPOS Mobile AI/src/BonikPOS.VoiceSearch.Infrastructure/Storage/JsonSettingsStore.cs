using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Settings;

namespace BonikPOS.VoiceSearch.Infrastructure.Storage;

/// <summary>Settings as JSON in the private data directory. Contains no secrets (tokens live in <see cref="ISecretStore"/>).</summary>
public sealed class JsonSettingsStore(IAppPaths paths) : ISettingsStore
{
    public const string FileName = "settings.json";
    private readonly string _path = Path.Combine(paths.DataDirectory, FileName);

    public async Task<VoiceSearchSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new VoiceSearchSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync(stream, InfrastructureJsonContext.Default.VoiceSearchSettings, cancellationToken).ConfigureAwait(false)
                   ?? new VoiceSearchSettings();
        }
        catch (JsonException)
        {
            return new VoiceSearchSettings(); // corrupt or older format: fall back to defaults
        }
    }

    public async Task SaveAsync(VoiceSearchSettings settings, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, InfrastructureJsonContext.Default.VoiceSearchSettings);
        await AtomicFile.WriteAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
    }
}
