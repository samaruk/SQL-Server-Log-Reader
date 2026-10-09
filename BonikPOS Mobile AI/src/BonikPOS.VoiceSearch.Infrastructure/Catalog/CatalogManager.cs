using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.Infrastructure.Catalog;

/// <summary>
/// Creates the catalog source selected in settings and loads it into the search service. A failing real catalog never
/// falls back to the demo catalog automatically (that would present demo data as stock); the error is surfaced and the
/// previously loaded catalog, if any, stays active.
/// </summary>
public sealed class CatalogManager(
    IMedicineSearchService search,
    SettingsService settings,
    ISecretStore secrets,
    Func<HttpClient> httpClient,
    ICatalogCache cache,
    ILoggerFactory? loggers = null) : ICatalogManager
{
    private readonly ILoggerFactory _loggers = loggers ?? NullLoggerFactory.Instance;

    public MedicineCatalogInfo Info => search.CatalogInfo;

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await search.LoadCatalogAsync(CreateSource(current), cancellationToken).ConfigureAwait(false);
        search.Thresholds = current.Search;
    }

    public IMedicineCatalogSource CreateSource(VoiceSearchSettings s) => s.CatalogSource switch
    {
        CatalogSourceKind.LocalFile when !string.IsNullOrWhiteSpace(s.LocalCatalogPath) && File.Exists(s.LocalCatalogPath)
            => JsonCatalogSource.FromFile(s.LocalCatalogPath),
        CatalogSourceKind.LocalFile => throw new CatalogUnavailableException("The local product index file was not found. Import it again in Settings."),
        CatalogSourceKind.BonikPosApi => new BonikPosApiCatalogSource(
            httpClient(), s.Api, () => secrets.GetAsync(SecretKeys.ApiToken), cache, _loggers.CreateLogger<BonikPosApiCatalogSource>()),
        _ => new DemoCatalogSource(),
    };
}
