using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Index;
using BonikPOS.VoiceSearch.MedicineSearch.Ranking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.MedicineSearch;

/// <summary><see cref="IMedicineSearchService"/> over an in-memory <see cref="MedicineIndex"/>.</summary>
public sealed class MedicineSearchService : IMedicineSearchService
{
    private readonly Func<IMedicineCatalogSource> _defaultSource;
    private readonly IMedicineSearchHints? _hints;
    private readonly ILogger _logger;
    private volatile State? _state;

    public MedicineSearchService(Func<IMedicineCatalogSource> defaultSource, IMedicineSearchHints? hints = null, ILogger<MedicineSearchService>? logger = null)
    {
        _defaultSource = defaultSource;
        _hints = hints;
        _logger = logger ?? NullLogger<MedicineSearchService>.Instance;
    }

    public MedicineCatalogInfo CatalogInfo => _state?.Info ?? MedicineCatalogInfo.NotLoaded;

    public SearchThresholds Thresholds { get; set; } = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => LoadCatalogAsync(_defaultSource(), cancellationToken);

    public async Task LoadCatalogAsync(IMedicineCatalogSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = await Task.Run(() => MedicineIndex.Build(snapshot.Products), cancellationToken).ConfigureAwait(false);
        var warnings = snapshot.Warnings.Concat(index.Warnings).ToList();
        foreach (var w in index.Warnings)
        {
            _logger.LogWarning("Catalog: {Warning}", w);
        }

        var info = new MedicineCatalogInfo(true, index.Products.Count, snapshot.SourceDescription, snapshot.IsDemoData, snapshot.RetrievedAt, warnings);
        _state = new State(index, new MedicineMatcher(index, _hints), info);
    }

    public Task<MedicineSearchResult> SearchAsync(MedicineQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var state = _state ?? throw new CatalogUnavailableException("The medicine catalog has not been loaded.");
        var thresholds = Thresholds;
        return Task.Run(() => state.Matcher.Search(query, thresholds, state.Info), cancellationToken);
    }

    public MedicineProduct? GetProduct(string productId) => _state?.Index.Find(productId)?.Product;

    private sealed record State(MedicineIndex Index, MedicineMatcher Matcher, MedicineCatalogInfo Info);
}
