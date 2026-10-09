using System.Net;
using System.Net.Http.Headers;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Infrastructure.Catalog;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Infrastructure;

public sealed class BonikPosApiCatalogSourceTests : IDisposable
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly TempDirectory _dir = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose()
    {
        _handler.Dispose();
        _dir.Dispose();
    }

    private static BonikPosApiSettings Settings(string? baseUrl = "https://pos.example.test/", int pageSize = 2, int retries = 2) => new()
    {
        BaseUrl = baseUrl,
        PageSize = pageSize,
        MaxRetries = retries,
        TimeoutSeconds = 30,
    };

    private BonikPosApiCatalogSource Source(BonikPosApiSettings? settings = null, string? token = "secret-token", ICatalogCache? cache = null) =>
        new(new HttpClient(_handler, disposeHandler: false), settings ?? Settings(), () => Task.FromResult(token), cache, logger: null,
            delay: (d, _) =>
            {
                _delays.Add(d); // recorded instead of waited for: tests never sleep
                return Task.CompletedTask;
            });

    private static string Item(string id, string brand, string? strength = "500 mg", string? form = "Tablet") =>
        $$"""{"productId": "{{id}}", "brandName": "{{brand}}", "genericName": "Paracetamol", "strength": "{{strength}}", "dosageForm": "{{form}}", "manufacturer": "Maker", "packSize": "10x10", "localName": "নাপা"}""";

    [Fact]
    public async Task Paging_FollowsHasMore_SendsTheTokenAndPagingParameters_AndMapsEveryField()
    {
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}, {{Item("P2", "Seclo", "20 mg", "Capsule")}}], "hasMore": true}""")
                .RespondJson($$"""{"items": [{{Item("P3", "Fexo", "120 mg")}}], "hasMore": false}""");

        var products = await Source().FetchAllAsync(Ct);

        Assert.Equal(["P1", "P2", "P3"], products.Select(p => p.ProductId));
        var p1 = products[0];
        Assert.Equal(("Napa", "Paracetamol", "500 mg", "Tablet", "Maker", "10x10", "নাপা"),
            (p1.BrandName, p1.GenericName, p1.Strength, p1.DosageForm, p1.Manufacturer, p1.PackSize, p1.LocalName));
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("https://pos.example.test/api/products?page=1&pageSize=2", _handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("https://pos.example.test/api/products?page=2&pageSize=2", _handler.Requests[1].RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret-token"), _handler.Requests[0].Headers.Authorization);
        Assert.Contains(_handler.Requests[0].Headers.Accept, a => a.MediaType == "application/json");
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task WithoutHasMore_PagingStopsAtTheFirstShortPage()
    {
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}, {{Item("P2", "Ace")}}]}""")
                .RespondJson($$"""{"items": [{{Item("P3", "Fast")}}]}""");

        var products = await Source().FetchAllAsync(Ct);

        Assert.Equal(3, products.Count);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task AnEmptyPage_EndsPaging_EvenIfTheServerClaimsMore()
    {
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}, {{Item("P2", "Ace")}}], "hasMore": true}""")
                .RespondJson("""{"items": [], "hasMore": true}""");

        Assert.Equal(2, (await Source().FetchAllAsync(Ct)).Count);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ConfiguredFieldNames_RootArrays_NumericIds_AndCaseInsensitiveKeys_AreMapped()
    {
        var settings = Settings(pageSize: 10) with
        {
            ProductsPath = "/v2/stock/items",
            Fields = new ProductFieldMapping
            {
                ItemsProperty = "",
                ProductId = "sku",
                BrandName = "name",
                GenericName = "molecule",
                Strength = "potency",
                DosageForm = "form",
                HasMoreProperty = "",
            },
        };
        _handler.RespondJson("""
            [
              {"SKU": 1001, "Name": "Napa", "molecule": "Paracetamol", "potency": "500 mg", "FORM": "Tablet"},
              {"sku": "1002", "name": "Seclo"},
              {"sku": "", "name": "No id"},
              {"sku": "1003"},
              {"sku": "1001", "name": "Duplicate id"},
              "not an object"
            ]
            """);

        var products = await Source(settings).FetchAllAsync(Ct);

        Assert.Equal("https://pos.example.test/v2/stock/items?page=1&pageSize=10", _handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(["1001", "1002"], products.Select(p => p.ProductId));
        Assert.Equal(("Napa", "Paracetamol", "500 mg", "Tablet"), (products[0].BrandName, products[0].GenericName, products[0].Strength, products[0].DosageForm));
        Assert.Null(products[1].Strength);
    }

    [Fact]
    public async Task ApiKeyScheme_SendsTheTokenInTheConfiguredHeader()
    {
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}]}""");

        await Source(Settings() with { AuthScheme = "ApiKey", ApiKeyHeader = "X-Bonik-Key" }).FetchAllAsync(Ct);

        Assert.Null(_handler.Requests[0].Headers.Authorization);
        Assert.Equal(["secret-token"], _handler.Requests[0].Headers.GetValues("X-Bonik-Key"));
    }

    [Fact]
    public async Task WithoutAToken_NoCredentialsAreSent()
    {
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}]}""");

        await Source(token: null).FetchAllAsync(Ct);

        Assert.Null(_handler.Requests[0].Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the credentials (HTTP 401)")]
    [InlineData(HttpStatusCode.Forbidden, "rejected the credentials (HTTP 403)")]
    [InlineData(HttpStatusCode.NotFound, "products endpoint was not found (HTTP 404)")]
    [InlineData(HttpStatusCode.BadRequest, "HTTP 400")]
    public async Task ClientErrors_FailImmediatelyWithAnActionableMessage(HttpStatusCode status, string expected)
    {
        _handler.RespondStatus(status);

        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source().FetchAllAsync(Ct));

        Assert.Contains(expected, ex.Message);
        Assert.Single(_handler.Requests); // not retried
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task ServerError_IsRetriedWithBackoff_AndTheSecondAttemptSucceeds()
    {
        _handler.RespondStatus(HttpStatusCode.InternalServerError)
                .RespondJson($$"""{"items": [{{Item("P1", "Napa")}}]}""");

        var products = await Source().FetchAllAsync(Ct);

        Assert.Single(products);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], _delays);
    }

    [Fact]
    public async Task PersistentServerErrors_GiveUpAfterMaxRetries_WithExponentialBackoff()
    {
        _handler.Fallback = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(Settings(retries: 2)).FetchAllAsync(Ct));

        Assert.Contains("HTTP 503", ex.Message);
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)], _delays);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(120, 10)] // the server's wish is capped at 10 s
    public async Task TooManyRequests_HonoursRetryAfter(int retryAfterSeconds, int expectedDelaySeconds)
    {
        _handler.RespondStatus(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds)))
                .RespondJson($$"""{"items": [{{Item("P1", "Napa")}}]}""");

        var products = await Source().FetchAllAsync(Ct);

        Assert.Single(products);
        Assert.Equal([TimeSpan.FromSeconds(expectedDelaySeconds)], _delays);
    }

    [Theory]
    [InlineData("{ \"items\": [ ")]
    [InlineData("<html>maintenance</html>")]
    public async Task InvalidJson_IsReportedClearly(string body)
    {
        _handler.RespondJson(body);

        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source().FetchAllAsync(Ct));

        Assert.Contains("invalid JSON", ex.Message);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ResponseWithoutTheItemsArray_PointsAtTheFieldSettings()
    {
        _handler.RespondJson("""{"data": []}""");

        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source().FetchAllAsync(Ct));

        Assert.Contains("no 'items' array", ex.Message);
    }

    [Fact]
    public async Task Timeout_IsRetried_ThenReportedAsATimeout()
    {
        // HttpClient reports its own timeout as a TaskCanceledException that the caller did not request.
        _handler.Fallback = (_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(Settings(retries: 1)).FetchAllAsync(Ct));

        Assert.Contains("did not answer within 30 s", ex.Message);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task NetworkFailure_IsRetried_ThenReported()
    {
        _handler.RespondFailure().RespondJson($$"""{"items": [{{Item("P1", "Napa")}}]}""");
        Assert.Single(await Source().FetchAllAsync(Ct));

        _handler.Requests.Clear();
        _handler.Fallback = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Name or service not known"));
        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(Settings(retries: 1)).FetchAllAsync(Ct));
        Assert.Contains("could not be reached", ex.Message);
        Assert.Contains("Name or service not known", ex.Message);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetriedOrDisguisedAsAnApiError()
    {
        using var cts = new CancellationTokenSource();
        _handler.Fallback = async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source().FetchAllAsync(cts.Token));

        Assert.Single(_handler.Requests);
        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData("http://pos.example.test/")]
    [InlineData("ftp://pos.example.test/")]
    [InlineData("http://192.168.1.20:8080/")] // a LAN address is not loopback
    public async Task NonHttpsAddresses_AreRejectedBeforeAnyRequest(string baseUrl)
    {
        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(Settings(baseUrl)).FetchAllAsync(Ct));

        Assert.Contains("must use HTTPS", ex.Message);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:8080/bonik")]
    [InlineData("http://[::1]:5000/")]
    public async Task PlainHttp_IsAllowedForLoopbackDevelopmentServers(string baseUrl)
    {
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}]}""");

        Assert.Single(await Source(Settings(baseUrl)).FetchAllAsync(Ct));
        Assert.StartsWith(baseUrl.TrimEnd('/') + "/api/products?page=1", _handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task MissingAddress_IsReportedAsNotConfigured(string? baseUrl)
    {
        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(Settings(baseUrl)).FetchAllAsync(Ct));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task SuccessfulSync_IsCached_AndAFailedSyncFallsBackToTheCacheWithAWarning()
    {
        var cache = new FileCatalogCache(_dir.AppPaths());
        _handler.RespondJson($$"""{"items": [{{Item("P1", "Napa")}}, {{Item("P2", "Seclo")}}], "hasMore": false}""");
        var fresh = await Source(cache: cache).LoadAsync(Ct);
        Assert.Equal(2, fresh.Products.Count);
        Assert.Empty(fresh.Warnings);
        Assert.False(fresh.IsDemoData);
        Assert.Equal("BonikPOS API (pos.example.test)", fresh.SourceDescription);

        _handler.Fallback = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        var fallback = await Source(cache: cache).LoadAsync(Ct);

        Assert.Equal(["P1", "P2"], fallback.Products.Select(p => p.ProductId));
        Assert.Equal("Seclo", fallback.Products[1].BrandName);
        Assert.EndsWith("(cached)", fallback.SourceDescription);
        var warning = Assert.Single(fallback.Warnings);
        Assert.Contains("Sync failed", warning);
        Assert.Contains("HTTP 502", warning);
        Assert.Equal(fresh.RetrievedAt, fallback.RetrievedAt);
    }

    [Fact]
    public async Task FailedSyncWithoutACache_Throws()
    {
        _handler.Fallback = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(cache: new FileCatalogCache(_dir.AppPaths())).LoadAsync(Ct));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => Source(cache: null).LoadAsync(Ct));
    }
}

internal static class StubHttpMessageHandlerExtensions
{
    public static StubHttpMessageHandler RespondFailure(this StubHttpMessageHandler handler) =>
        handler.Respond((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));
}
