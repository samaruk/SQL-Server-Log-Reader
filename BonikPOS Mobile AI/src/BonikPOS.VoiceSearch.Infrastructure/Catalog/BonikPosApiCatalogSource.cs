using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.Infrastructure.Catalog;

/// <summary>
/// Adapter for the existing BonikPOS product API. The endpoint, paging parameters, authentication scheme and JSON
/// field names are configuration (<see cref="BonikPosApiSettings"/>), because the real schema was not available when
/// this adapter was written - see docs/BACKEND_INTEGRATION.md. Products are cached locally so search keeps working
/// offline; if a sync fails and a cache exists, the cached catalog is used and the failure is reported as a warning.
/// </summary>
public sealed class BonikPosApiCatalogSource : IMedicineCatalogSource
{
    public const int MaxPages = 2000;
    private readonly HttpClient _http;
    private readonly BonikPosApiSettings _settings;
    private readonly Func<Task<string?>> _token;
    private readonly ICatalogCache? _cache;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public BonikPosApiCatalogSource(
        HttpClient http,
        BonikPosApiSettings settings,
        Func<Task<string?>> tokenProvider,
        ICatalogCache? cache = null,
        ILogger<BonikPosApiCatalogSource>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _settings = settings;
        _token = tokenProvider;
        _cache = cache;
        _logger = logger ?? NullLogger<BonikPosApiCatalogSource>.Instance;
        _delay = delay ?? Task.Delay;
    }

    public string Description => $"BonikPOS API ({TryHost()})";

    public bool IsDemoData => false;

    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var products = await FetchAllAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = new CatalogSnapshot(products, DateTimeOffset.UtcNow, Description, false, []);
            if (_cache is not null)
            {
                await _cache.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            return snapshot;
        }
        catch (CatalogUnavailableException ex) when (_cache is not null)
        {
            var cached = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (cached is null)
            {
                throw;
            }

            _logger.LogWarning(ex, "Catalog sync failed; using cached catalog from {Date}", cached.RetrievedAt);
            return cached with
            {
                Warnings = [.. cached.Warnings, $"Sync failed ({ex.Message}). Showing the catalog cached on {cached.RetrievedAt:yyyy-MM-dd HH:mm}."],
            };
        }
    }

    public async Task<IReadOnlyList<MedicineProduct>> FetchAllAsync(CancellationToken cancellationToken)
    {
        var baseUri = ValidateBaseUrl(_settings.BaseUrl);
        string? token = await _token().ConfigureAwait(false);
        var products = new List<MedicineProduct>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 1; page <= MaxPages; page++)
        {
            var uri = new Uri(baseUri, $"{_settings.ProductsPath.TrimStart('/')}?{Uri.EscapeDataString(_settings.PageParameter)}={page}&{Uri.EscapeDataString(_settings.PageSizeParameter)}={_settings.PageSize}");
            using var doc = await GetJsonWithRetryAsync(uri, token, cancellationToken).ConfigureAwait(false);
            var (items, hasMore) = ReadPage(doc.RootElement);
            int before = products.Count;
            foreach (var item in items)
            {
                var p = MapProduct(item);
                if (p is not null && seen.Add(p.ProductId))
                {
                    products.Add(p);
                }
            }

            bool shortPage = items.Count < _settings.PageSize;
            if (hasMore == false || (hasMore is null && shortPage) || products.Count == before && items.Count == 0)
            {
                return products;
            }
        }

        throw new CatalogUnavailableException($"The API returned more than {MaxPages} pages; check the paging settings.");
    }

    internal MedicineProduct? MapProduct(JsonElement item)
    {
        var f = _settings.Fields;
        string? id = Str(item, f.ProductId);
        string? brand = Str(item, f.BrandName);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(brand))
        {
            return null;
        }

        return new MedicineProduct
        {
            ProductId = id,
            BrandName = brand,
            GenericName = Str(item, f.GenericName),
            Strength = Str(item, f.Strength),
            DosageForm = Str(item, f.DosageForm),
            Manufacturer = Str(item, f.Manufacturer),
            PackSize = Str(item, f.PackSize),
            LocalName = Str(item, f.LocalName),
        };
    }

    private (IReadOnlyList<JsonElement> Items, bool? HasMore) ReadPage(JsonElement root)
    {
        var f = _settings.Fields;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(f.ItemsProperty) && TryGet(root, f.ItemsProperty) is { ValueKind: JsonValueKind.Array } a)
        {
            array = a;
        }
        else
        {
            throw new CatalogUnavailableException($"The API response has no '{f.ItemsProperty}' array. Adjust Settings > Catalog > field names.");
        }

        bool? hasMore = null;
        if (!string.IsNullOrEmpty(f.HasMoreProperty) && root.ValueKind == JsonValueKind.Object && TryGet(root, f.HasMoreProperty) is { } hm
            && hm.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            hasMore = hm.GetBoolean();
        }

        return (array.EnumerateArray().ToList(), hasMore);
    }

    private async Task<JsonDocument> GetJsonWithRetryAsync(Uri uri, string? token, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(token))
            {
                if (string.Equals(_settings.AuthScheme, "ApiKey", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(_settings.ApiKeyHeader, token);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < _settings.MaxRetries)
                {
                    await BackoffAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new CatalogUnavailableException($"The BonikPOS API did not answer within {_settings.TimeoutSeconds} s.");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < _settings.MaxRetries)
                {
                    await BackoffAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new CatalogUnavailableException($"The BonikPOS API could not be reached: {ex.Message}", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                    }
                    catch (JsonException ex)
                    {
                        throw new CatalogUnavailableException("The BonikPOS API returned invalid JSON.", ex);
                    }
                }

                var status = response.StatusCode;
                bool transient = status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
                if (transient && attempt < _settings.MaxRetries)
                {
                    await BackoffAsync(attempt, response.Headers.RetryAfter?.Delta, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new CatalogUnavailableException(status switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        $"The BonikPOS API rejected the credentials (HTTP {(int)status}). Check the API token in Settings.",
                    HttpStatusCode.NotFound => $"The products endpoint was not found (HTTP 404). Check the products path '{_settings.ProductsPath}'.",
                    _ => $"The BonikPOS API returned HTTP {(int)status} {response.ReasonPhrase}.",
                });
            }
        }
    }

    private Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
        return _delay(delay < TimeSpan.FromSeconds(10) ? delay : TimeSpan.FromSeconds(10), cancellationToken);
    }

    private static Uri ValidateBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var uri))
        {
            throw new CatalogUnavailableException("The BonikPOS API address is not configured. Enter it in Settings > Catalog.");
        }

        bool loopback = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(loopback && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new CatalogUnavailableException("The BonikPOS API address must use HTTPS.");
        }

        return uri;
    }

    private string TryHost() => Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out var u) ? u.Host : "not configured";

    private static JsonElement? TryGet(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (string.IsNullOrEmpty(name) || obj.ValueKind != JsonValueKind.Object || TryGet(obj, name) is not { } v)
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }
}

/// <summary>Local copy of the last successful catalog sync.</summary>
public interface ICatalogCache
{
    Task SaveAsync(CatalogSnapshot snapshot, CancellationToken cancellationToken);

    Task<CatalogSnapshot?> LoadAsync(CancellationToken cancellationToken);
}

public sealed class FileCatalogCache(IAppPaths paths) : ICatalogCache
{
    private readonly string _path = Path.Combine(paths.CacheDirectory, "catalog-cache.json");

    public async Task SaveAsync(CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        var file = new CatalogCacheFile(snapshot.RetrievedAt, snapshot.SourceDescription, snapshot.Products);
        await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(file, InfrastructureJsonContext.Default.CatalogCacheFile), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CatalogSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var s = File.OpenRead(_path);
            var file = await JsonSerializer.DeserializeAsync(s, InfrastructureJsonContext.Default.CatalogCacheFile, cancellationToken).ConfigureAwait(false);
            return file is null ? null : new CatalogSnapshot(file.Products, file.RetrievedAt, file.SourceDescription + " (cached)", false, []);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
