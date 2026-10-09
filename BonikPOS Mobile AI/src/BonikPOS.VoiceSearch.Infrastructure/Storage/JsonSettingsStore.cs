using System.Text.Json;
using System.Text.Json.Nodes;
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
            // The source-generated serializer sets every init-only property that is missing from the file (for example
            // one added in a later app version) to default(T) - null nested settings, RequireOwnerMatch = false - instead
            // of its declared default. The stored values are therefore laid over the serialized defaults first.
            var merged = JsonSerializer.SerializeToNode(new VoiceSearchSettings(), InfrastructureJsonContext.Default.VoiceSearchSettings)!.AsObject();
            await using var stream = File.OpenRead(_path);
            if (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) is JsonObject stored)
            {
                Overlay(merged, stored);
            }

            return JsonSerializer.Deserialize(merged, InfrastructureJsonContext.Default.VoiceSearchSettings) ?? new VoiceSearchSettings();
        }
        catch (JsonException)
        {
            return new VoiceSearchSettings(); // corrupt or older format: fall back to defaults
        }
    }

    private static void Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source.ToList())
        {
            if (value is JsonObject child && target[name] is JsonObject existing)
            {
                Overlay(existing, child);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    public async Task SaveAsync(VoiceSearchSettings settings, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, InfrastructureJsonContext.Default.VoiceSearchSettings);
        await AtomicFile.WriteAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
    }
}
