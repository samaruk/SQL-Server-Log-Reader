using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Recognition;

namespace BonikPOS.VoiceSearch.Core.Settings;

public enum CatalogSourceKind
{
    /// <summary>Clearly labelled demo catalog bundled with the app (not real stock data).</summary>
    Demo,
    /// <summary>A product index file exported from BonikPOS (JSON), stored on the device.</summary>
    LocalFile,
    /// <summary>The BonikPOS product API (synced to a local cache for offline search).</summary>
    BonikPosApi,
}

public enum AcceleratorPreference
{
    /// <summary>Try the platform accelerator (XNNPACK on Android, CoreML on iOS); fall back to CPU.</summary>
    Auto,
    CpuOnly,
}

public enum ListeningMode
{
    /// <summary>Listen until one owner utterance has been processed, then stop (default).</summary>
    SingleQuery,
    /// <summary>Keep listening and process every owner utterance until stopped.</summary>
    Continuous,
}

public sealed record OwnerVerificationSettings
{
    /// <summary>When true, utterances not attributed to the owner are never recognised or searched.</summary>
    public bool RequireOwnerMatch { get; init; } = true;

    /// <summary>Overrides the threshold suggested at enrollment. Null = use the enrollment calibration.</summary>
    public double? AcceptThresholdOverride { get; init; }

    /// <summary>Gap below the accept threshold that is still treated as "uncertain" rather than "not owner".</summary>
    public double UncertainBand { get; init; } = 0.08;

    /// <summary>A window this far below the accept threshold inside an accepted utterance flags a second voice.</summary>
    public double WindowDrop { get; init; } = 0.15;
}

public sealed record PipelineSettings
{
    public ListeningMode Mode { get; init; } = ListeningMode.SingleQuery;

    public double VadStartThreshold { get; init; } = 0.5;

    public double VadEndThreshold { get; init; } = 0.35;

    public int MinSpeechMilliseconds { get; init; } = 250;

    public int EndSilenceMilliseconds { get; init; } = 500;

    public int PreRollMilliseconds { get; init; } = 300;

    public int MaxUtteranceMilliseconds { get; init; } = 8000;

    /// <summary>In single-query mode, stop listening if no owner speech arrives within this time.</summary>
    public int MaxListenMilliseconds { get; init; } = 15000;

    public int QueueCapacityFrames { get; init; } = 150;
}

public sealed record BonikPosApiSettings
{
    /// <summary>Base address of the BonikPOS backend, e.g. https://pos.example.com/. Must be HTTPS.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Relative path of the product list endpoint.</summary>
    public string ProductsPath { get; init; } = "api/products";

    public int PageSize { get; init; } = 500;

    public string PageParameter { get; init; } = "page";

    public string PageSizeParameter { get; init; } = "pageSize";

    /// <summary>"Bearer" (Authorization header) or "ApiKey" (custom header).</summary>
    public string AuthScheme { get; init; } = "Bearer";

    public string ApiKeyHeader { get; init; } = "X-Api-Key";

    public int TimeoutSeconds { get; init; } = 30;

    public int MaxRetries { get; init; } = 2;

    /// <summary>JSON property names in the API response. Adjust to the real BonikPOS schema.</summary>
    public ProductFieldMapping Fields { get; init; } = new();
}

public sealed record ProductFieldMapping
{
    /// <summary>Property holding the product array. Empty when the response body is the array itself.</summary>
    public string ItemsProperty { get; init; } = "items";

    public string ProductId { get; init; } = "productId";

    public string BrandName { get; init; } = "brandName";

    public string GenericName { get; init; } = "genericName";

    public string Strength { get; init; } = "strength";

    public string DosageForm { get; init; } = "dosageForm";

    public string Manufacturer { get; init; } = "manufacturer";

    public string PackSize { get; init; } = "packSize";

    public string LocalName { get; init; } = "localName";

    /// <summary>Property telling whether more pages exist. Empty = keep paging until a short page.</summary>
    public string HasMoreProperty { get; init; } = "hasMore";
}

/// <summary>All user-adjustable settings. Secrets (API tokens) are kept separately in <see cref="ISecretStore"/>.</summary>
public sealed record VoiceSearchSettings
{
    public RecognitionLanguageMode LanguageMode { get; init; } = RecognitionLanguageMode.AutoBanglaEnglish;

    public SearchThresholds Search { get; init; } = new();

    public OwnerVerificationSettings Verification { get; init; } = new();

    public PipelineSettings Pipeline { get; init; } = new();

    public AudioInputPreference AudioInput { get; init; } = AudioInputPreference.VoiceRecognition;

    public AcceleratorPreference Accelerator { get; init; } = AcceleratorPreference.Auto;

    /// <summary>ONNX Runtime intra-op threads; 0 = automatic (min(4, cores)).</summary>
    public int InferenceThreads { get; init; }

    /// <summary>Whether to use target-speaker extraction when a compatible model is installed.</summary>
    public bool TargetSpeakerExtractionEnabled { get; init; } = true;

    public bool HistoryEnabled { get; init; } = true;

    public CatalogSourceKind CatalogSource { get; init; } = CatalogSourceKind.Demo;

    public string? LocalCatalogPath { get; init; }

    public BonikPosApiSettings Api { get; init; } = new();
}

public interface ISettingsStore
{
    Task<VoiceSearchSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(VoiceSearchSettings settings, CancellationToken cancellationToken);
}

/// <summary>Platform secure storage (Android Keystore / iOS Keychain) for small secrets such as API tokens and data keys.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    Task RemoveAsync(string key);
}

public static class SecretKeys
{
    public const string ApiToken = "bonikpos.api.token";
    public const string ProfileDataKey = "bonikpos.profile.datakey.v1";
    public const string HistoryDataKey = "bonikpos.history.datakey.v1";
}
