using System.Text.Json.Serialization;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;

namespace BonikPOS.VoiceSearch.Infrastructure.Storage;

/// <summary>Model manifest file (models/models.json).</summary>
public sealed record ModelManifestFile(string Format, IReadOnlyList<ModelDescriptor> Models);

public sealed record ChecksumCacheEntry(string Path, long Length, long LastWriteTicks, string Sha256);

public sealed record CatalogCacheFile(DateTimeOffset RetrievedAt, string SourceDescription, IReadOnlyList<MedicineProduct> Products);

/// <summary>Source-generated serializers: no reflection, safe for trimmed iOS/Android builds.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(VoiceProfile))]
[JsonSerializable(typeof(List<SearchHistoryEntry>))]
[JsonSerializable(typeof(VoiceSearchSettings))]
[JsonSerializable(typeof(ModelManifestFile))]
[JsonSerializable(typeof(List<ChecksumCacheEntry>))]
[JsonSerializable(typeof(CatalogCacheFile))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext;
