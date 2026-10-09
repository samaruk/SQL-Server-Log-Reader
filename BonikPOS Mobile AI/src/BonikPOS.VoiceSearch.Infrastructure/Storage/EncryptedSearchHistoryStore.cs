using System.Security.Cryptography;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Infrastructure.Security;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.Infrastructure.Storage;

/// <summary>
/// Encrypted search history (no audio). Also provides ranking hints: products the owner confirmed for an identical
/// query text before, and confirmation counts for tie-breaking.
/// </summary>
public sealed class EncryptedSearchHistoryStore : ISearchHistoryStore, IMedicineSearchHints
{
    public const string FileName = "search-history.bin";
    public const int MaxEntries = 500;
    private readonly string _path;
    private readonly AesGcmDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<SearchHistoryEntry>? _entries;
    private volatile Hints _hints = Hints.Empty;

    public EncryptedSearchHistoryStore(IAppPaths paths, ISecretStore secrets)
    {
        _path = Path.Combine(paths.DataDirectory, FileName);
        _protector = new AesGcmDataProtector(secrets, SecretKeys.HistoryDataKey, "bonikpos.search-history.v1");
    }

    public async Task AddAsync(SearchHistoryEntry entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            await SaveUnlockedAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> GetRecentAsync(int maxEntries, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return entries.Take(maxEntries).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries = [];
            _hints = Hints.Empty;
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            await _protector.DestroyKeyAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads history so that ranking hints are available before the first search.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public int GetPopularity(string productId) => _hints.Popularity.GetValueOrDefault(productId);

    public IReadOnlyList<string> GetLearnedProducts(string normalizedQuery) =>
        _hints.Learned.TryGetValue(normalizedQuery, out var ids) ? ids : [];

    private async Task<List<SearchHistoryEntry>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = [];
        if (File.Exists(_path))
        {
            try
            {
                var plaintext = await _protector.UnprotectAsync(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
                _entries = JsonSerializer.Deserialize(plaintext, InfrastructureJsonContext.Default.ListSearchHistoryEntry) ?? [];
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                // Unreadable history is not critical: start fresh instead of blocking search.
                _entries = [];
            }
        }

        RebuildHints(_entries);
        return _entries;
    }

    private async Task SaveUnlockedAsync(List<SearchHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries, InfrastructureJsonContext.Default.ListSearchHistoryEntry);
        var envelope = await _protector.ProtectAsync(plaintext).ConfigureAwait(false);
        await AtomicFile.WriteAsync(_path, envelope, cancellationToken).ConfigureAwait(false);
        RebuildHints(entries);
    }

    private void RebuildHints(List<SearchHistoryEntry> entries)
    {
        var popularity = new Dictionary<string, int>(StringComparer.Ordinal);
        var learned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.SelectedProductId is null || e.Outcome is not (HistoryOutcome.Confirmed or HistoryOutcome.Corrected))
            {
                continue;
            }

            popularity[e.SelectedProductId] = popularity.GetValueOrDefault(e.SelectedProductId) + 1;
            string key = TextNormalizer.Compact(TextNormalizer.Normalize(e.QueryText));
            if (key.Length == 0)
            {
                continue;
            }

            if (!learned.TryGetValue(key, out var list))
            {
                learned[key] = list = [];
            }

            if (!list.Contains(e.SelectedProductId))
            {
                list.Add(e.SelectedProductId);
            }
        }

        _hints = new Hints(popularity, learned.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal));
    }

    private sealed record Hints(IReadOnlyDictionary<string, int> Popularity, IReadOnlyDictionary<string, IReadOnlyList<string>> Learned)
    {
        public static Hints Empty { get; } = new(new Dictionary<string, int>(), new Dictionary<string, IReadOnlyList<string>>());
    }
}
