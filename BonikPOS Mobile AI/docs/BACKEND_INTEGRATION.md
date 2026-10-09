# BonikPOS backend integration

How the app obtains the medicine catalog from BonikPOS, as implemented in this repository. Paths are relative to the
solution root (the folder that contains `Directory.Build.props`).

**Status.** The endpoint, paging, authentication and JSON field names are configuration, "because the real schema was
not available when this adapter was written" (`BonikPosApiCatalogSource` remarks). The adapter has been tested only
against scripted HTTP responses
(`tests/BonikPOS.VoiceSearch.UnitTests/Infrastructure/BonikPosApiCatalogSourceTests.cs`), never against a real
BonikPOS server. Host names and responses below are examples, not a real BonikPOS API.

Related: [ARCHITECTURE.md](ARCHITECTURE.md), [PRIVACY.md](PRIVACY.md), [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md).

## 1. Catalog sources and when the catalog loads

`VoiceSearchSettings.CatalogSource` (Settings > "Medicine catalog") selects one source. `CatalogManager.CreateSource`
(`src/BonikPOS.VoiceSearch.Infrastructure/Catalog/CatalogManager.cs`) builds it:

| `CatalogSourceKind` | Settings label | Implementation | Notes |
|---|---|---|---|
| `Demo` (default) | "Demo catalog (test data)" | `DemoCatalogSource` | Embedded `src/BonikPOS.VoiceSearch.MedicineSearch/Catalog/demo-catalog.json`: 55 illustrative products with `DEMO-` ids, labelled as demo data in the UI. |
| `LocalFile` | "Local product index file" | `JsonCatalogSource.FromFile(LocalCatalogPath)` | A JSON product index imported on the phone (§8.2). A missing file gives "The local product index file was not found. Import it again in Settings." |
| `BonikPosApi` | "BonikPOS API" | `BonikPosApiCatalogSource` (`src/BonikPOS.VoiceSearch.Infrastructure/Catalog/BonikPosApiCatalogSource.cs`) | This document. |

`CatalogManager.ReloadAsync` loads the selected source into `MedicineSearchService` and then applies the search
thresholds from settings. It runs:

- when the search screen first prepares and no catalog is loaded (normally once after app start);
- on "Save settings";
- on "Sync catalog now".

There is no background or periodic sync, and each sync downloads the whole catalog. A failing source never falls back
to the demo catalog: `MedicineSearchService.LoadCatalogAsync` replaces the active index only after the new catalog has
loaded and been indexed, so the previously loaded catalog stays active. The API source has its own cache fallback (§6).

## 2. Configuration

`BonikPosApiSettings` (`src/BonikPOS.VoiceSearch.Core/Settings/VoiceSearchSettings.cs`) is `VoiceSearchSettings.Api`.
It is saved in `settings.json` in the private data directory (`JsonSettingsStore`: camelCase keys, enums as strings).
The token is not part of it (§3).

| Property (JSON key in `api`) | Default | On the Settings screen | Meaning |
|---|---|---|---|
| `BaseUrl` (`baseUrl`) | not set | "BonikPOS API base URL (https)" | Absolute URL. Must be `https`; `http` is accepted only for a loopback host (§7). A trailing `/` is added and a path is kept: `https://host/bonik` gives `https://host/bonik/api/products`. |
| `ProductsPath` (`productsPath`) | `api/products` | "Products path" | Relative to `BaseUrl`; a leading `/` is removed. Must not contain a query string, because the adapter appends `?…`. |
| `PageSize` (`pageSize`) | 500 | no | Sent as the page size; also the threshold for a "short" page (§5.3). |
| `PageParameter` (`pageParameter`) | `page` | no | Page number; the first page is 1. |
| `PageSizeParameter` (`pageSizeParameter`) | `pageSize` | no | |
| `AuthScheme` (`authScheme`) | `Bearer` | "Authentication" (`Bearer` or `ApiKey`) | `ApiKey` (any letter case) sends the token in `ApiKeyHeader`; any other value sends `Authorization: Bearer <token>`. |
| `ApiKeyHeader` (`apiKeyHeader`) | `X-Api-Key` | "API key header (for ApiKey)" | |
| `TimeoutSeconds` (`timeoutSeconds`) | 30 | no | Per attempt, covering the response headers and the body; at least 1. |
| `MaxRetries` (`maxRetries`) | 2 | no | Retries after the first attempt (3 attempts in total). |
| `Fields` (`fields`) | §5.2 | partly | JSON property names in the response (`ProductFieldMapping`). |

Values that are not on the Settings screen keep their code defaults; the app offers no settings import, so changing
them means changing the defaults in code or the stored `settings.json`.

## 3. Credentials

- Entered on the Settings screen in a password field ("Token or API key (stored in the phone's secure storage)"). On
  "Save settings", `SettingsViewModel.SaveAsync` trims the value and stores it with `ISecretStore.SetAsync` under the
  key `bonikpos.api.token` (`SecretKeys.ApiToken`). The implementation is `MauiSecretStore`, which uses MAUI
  `SecureStorage` (Android Keystore-backed, iOS Keychain).
- The token is never written to `settings.json`, the catalog cache or logs, and the screen never shows it: when a token
  exists the field shows `••••••••`; leaving that placeholder keeps the token, and clearing the field before saving
  removes it. "Delete all app data" also removes it.
- `CatalogManager` passes `() => secrets.GetAsync(SecretKeys.ApiToken)`; the adapter reads it once at the start of each
  sync. Without a token, requests carry no credentials.
- Not implemented: token expiry or refresh, OAuth sign-in, client certificates.

## 4. Request

For page = 1, 2, … (at most `BonikPosApiCatalogSource.MaxPages` = 2000):

```
GET {BaseUrl}/{ProductsPath}?{PageParameter}={page}&{PageSizeParameter}={PageSize}
Accept: application/json
Authorization: Bearer <token>        (AuthScheme "Bearer" and a token is stored)
<ApiKeyHeader>: <token>              (AuthScheme "ApiKey" and a token is stored)
```

With the defaults and the example base URL `https://pos.example.com/` the first request is
`GET https://pos.example.com/api/products?page=1&pageSize=500`. Parameter names are URL-escaped. There is no request
body, and only GET is used: the app never writes to the backend (a product the owner selects is recorded only in local
history).

## 5. Response mapping

### 5.1 Page shape

- A JSON array as the body is used as the product list.
- Otherwise the body must be an object with an array in the property named by `Fields.ItemsProperty` (default `items`,
  matched case-insensitively, top level only). Without it: "The API response has no 'items' array. Adjust Settings >
  Catalog > field names."
- `Fields.HasMoreProperty` (default `hasMore`, top level, case-insensitive) is used when it is a JSON boolean. Set it to
  an empty string to ignore it.

### 5.2 Product fields

| `MedicineProduct` property | Mapping property (default JSON name) | On the Settings screen |
|---|---|---|
| `ProductId` (required) | `ProductId` (`productId`) | "Product id" |
| `BrandName` (required) | `BrandName` (`brandName`) | "Brand name" |
| `GenericName` | `GenericName` (`genericName`) | "Generic name" |
| `Strength` | `Strength` (`strength`) | "Strength" |
| `DosageForm` | `DosageForm` (`dosageForm`) | "Dosage form" |
| `Manufacturer` | `Manufacturer` (`manufacturer`) | no |
| `PackSize` | `PackSize` (`packSize`) | no |
| `LocalName` | `LocalName` (`localName`) | no |
| `Aliases` | not read from the API | – |

Rules (`BonikPosApiCatalogSource.MapProduct`):

- Property names match case-insensitively, and only top-level properties of each item are read (no nested paths such
  as `brand.name`). Other properties are ignored.
- JSON strings are kept verbatim (not trimmed or rewritten); JSON numbers become their raw text (`1001` → `"1001"`);
  other value kinds (object, array, boolean, null) count as missing.
- Items that are not objects, or whose id or brand name is missing or blank, are skipped without a warning.
- An id already seen in the same sync is skipped (the first occurrence wins; ids compare case-sensitively).

How the app uses the fields: brand, local name and generic name are searchable names; strength and dosage form are
parsed for matching (`StrengthParser`, `MedicineVocabulary.ParseCatalogForm`) and displayed as written; the product id
is shown when a product is selected and stored in history; manufacturer and pack size are kept and cached but neither
searched nor shown by the current UI.

### 5.3 Paging

After each page the adapter stops when:

- `hasMore` is `false`; or
- `hasMore` is absent or not a boolean (or `HasMoreProperty` is empty) and the page holds fewer items than `PageSize`; or
- the page holds no items, even if `hasMore` is `true`; or
- every product on the page was already received (the server ignores the page parameter or repeats its last page); the
  snapshot then carries the warning "Page N of the API repeated products already received, so paging stopped. …".

Otherwise it requests the next page. After 2000 pages it fails with "The API returned more than 2000 pages; check the
paging settings." When the very first page is shorter than `PageSize` and has no `hasMore`, the catalog loads with the
warning "The API sent N products in one page (P requested) without a 'hasMore' flag, …", because a server that caps
its page size below `PageSize` looks exactly like a one-page catalog. Warnings are shown with the catalog status in
Settings.

## 6. Errors, retries, timeouts and the cache

| Situation | Behaviour (message in `CatalogUnavailableException`) |
|---|---|
| `BaseUrl` missing or not an absolute URL | No request. "The BonikPOS API address is not configured. Enter it in Settings > Catalog." |
| Scheme is not `https` (and not `http` to a loopback host) | No request. "The BonikPOS API address must use HTTPS." |
| No response headers within `TimeoutSeconds` | Retried. Finally: "The BonikPOS API did not answer within N s." |
| Network failure (`HttpRequestException`) | Retried. Finally: "The BonikPOS API could not be reached: …" |
| HTTP 408, 429 or 5xx | Retried. Finally: "The BonikPOS API returned HTTP …" |
| HTTP 401 or 403 | Not retried. "The BonikPOS API rejected the credentials (HTTP 401 / 403). Check the API token in Settings." |
| HTTP 404 | Not retried. "The products endpoint was not found (HTTP 404). Check the products path '…'." |
| Any other non-success status | Not retried. "The BonikPOS API returned HTTP <code> <reason>." |
| Body is not valid JSON | Not retried. "The BonikPOS API returned invalid JSON." |
| Body stops arriving within `TimeoutSeconds`, or the connection drops while it is read | Retried. Finally: "The BonikPOS API did not finish sending the catalog within N s." / "The connection to the BonikPOS API was interrupted: …" |
| The caller cancels | `OperationCanceledException`; not retried and not reported as an API error |

- Backoff before a retry: 250 ms × 2^attempt (250 ms, 500 ms, …), or the server's `Retry-After` delta when it sends
  one, capped at 10 s.
- Each attempt gets its own timeout token (`TimeoutSeconds`, linked to the caller's token). The shared `HttpClient`
  registered in `MauiProgram.cs` has `Timeout = Timeout.InfiniteTimeSpan`, so only the adapter's timeout applies.
- **Cache** (`FileCatalogCache`, defined in `BonikPosApiCatalogSource.cs`): after a successful sync the snapshot is written to
  `catalog-cache.json` in `FileSystem.CacheDirectory/bonikpos` (plain JSON) before it is returned. When a sync fails
  with a `CatalogUnavailableException` (every row above except cancellation) and a cache exists, the cached catalog is
  used with the source "BonikPOS API (<host>) (cached)", its original retrieval time, and the warning "Sync failed
  (<reason>). Showing the catalog cached on <yyyy-MM-dd HH:mm>.", which the Settings screen shows. Without a cache the
  error reaches the UI: "The medicine catalog could not be loaded: …" on the search screen, "Catalog sync failed: …" or
  "Could not save settings: …" in Settings.

## 7. Security and what is sent

- **Transport**: `ValidateBaseUrl` accepts `https`, and `http` only when the host is a loopback address (`localhost`,
  `127.0.0.1`, `[::1]`) for development; LAN addresses such as `http://192.168.1.20:8080/` are rejected (tests
  `NonHttpsAddresses_AreRejectedBeforeAnyRequest`, `PlainHttp_IsAllowedForLoopbackDevelopmentServers`). TLS validation
  is the platform default (no custom validation callback); certificate pinning is not implemented.
- **Sent**: only the GET request of §4: the URL with paging parameters, `Accept` and the credential header. The app adds
  no audio, recognised text, search history, voice profile or device information. The platform HTTP stack's own
  default headers were not inspected.
- **Stored**: the product list in memory and in the cache file (unencrypted); the token in secure storage.
- **Visible**: the configured host name is part of the catalog source description ("BonikPOS API (host)") shown on the
  search, Settings and Diagnostics screens, and included in the diagnostics report the owner can copy.

## 8. Data contracts

### 8.1 `MedicineProduct`

`src/BonikPOS.VoiceSearch.Core/Medicine/MedicineContracts.cs`: "A product exactly as the catalog source returned it.
Values are never rewritten or merged; the index keeps its own normalised copies for matching."

| Property | Type | Required | Notes |
|---|---|---|---|
| `ProductId` | string | yes | Must be unique; a repeated id keeps only its first row (the index reports a warning). Shown on selection and stored in history. |
| `BrandName` | string | yes | |
| `GenericName` | string | no | Combinations separated by `+` are also indexed per molecule; a parenthesised synonym is indexed too ("Vitamin C (Ascorbic Acid)"). |
| `Strength` | string | no | As written, e.g. "500 mg", "120 mg/5 ml", "500 mg + 65 mg", "0.05%", "100 mcg/puff", "1 g" (`StrengthParser`). |
| `DosageForm` | string | no | As written, e.g. "Tablet", "Film Coated Tablet", "Powder for Suspension". |
| `Manufacturer` | string | no | Not used for matching. |
| `PackSize` | string | no | Not used for matching. |
| `LocalName` | string | no | Local-script name, e.g. the Bangla spelling of the brand. |
| `Aliases` | string list | no | Read from local JSON files only. |

### 8.2 Local JSON product index

`JsonCatalogSource` (`src/BonikPOS.VoiceSearch.MedicineSearch/Catalog/CatalogSources.cs`) reads either a top-level
array of products or an object with a `products` (or `items`) array; other top-level properties such as `notice` are
ignored. Property names are matched case-insensitively; for each field the first name present is used:

| Field | Accepted property names |
|---|---|
| `ProductId` | `productId`, `id`, `product_id`, `sku` |
| `BrandName` | `brandName`, `brand`, `name`, `productName`, `brand_name` |
| `GenericName` | `genericName`, `generic`, `generic_name` |
| `Strength` | `strength` |
| `DosageForm` | `dosageForm`, `form`, `dosage_form` |
| `Manufacturer` | `manufacturer`, `company` |
| `PackSize` | `packSize`, `pack_size` |
| `LocalName` | `localName`, `banglaName`, `local_name` |
| `Aliases` | `aliases` (array of strings) |

Strings are kept verbatim and numbers become their raw text. A row without an id or brand name is skipped with the
warning "Row N has no product id or brand name and was skipped."

- **On the phone**: Settings > "Import product index (JSON)" opens a file picker (Android types `application/json`,
  `text/plain`; iOS `public.json`, `public.plain-text`), copies the file to
  `{data}/imported-catalog.json` (the private, backup-excluded data folder), and switches the source to "Local product index file". The
  catalog loads on "Save settings".
- **Evaluation tool**: `make-catalog --out products.json [--count 20000] [--seed 20261009]`
  (`tools/BonikPOS.VoiceSearch.Evaluation`, `RealisticSyntheticCatalog.Write`) writes this format as
  `{"notice": "SYNTHETIC CATALOG …", "products": [{"productId", "brandName", "genericName", "strength", "dosageForm"}, …]}`
  with invented brand names; `search --catalog products.json` reads a file in this format, so a real BonikPOS export
  can be evaluated the same way. How to run the tool: [SEARCH_EVALUATION.md §6](SEARCH_EVALUATION.md#6-running-it).
- The demo catalog uses the same format (`notice` plus `products`).

### 8.3 Catalog cache file

`CatalogCacheFile` (`src/BonikPOS.VoiceSearch.Infrastructure/Storage/InfrastructureJsonContext.cs`):
`{"retrievedAt": "<ISO 8601 time>", "sourceDescription": "BonikPOS API (<host>)", "products": [<MedicineProduct with
camelCase names; null values omitted>]}`. This is an internal file, not an exchange format.

## 9. Examples

These are illustrative only. `pos.example.com` is not a BonikPOS endpoint, and the product rows are invented sample data.

### 9.1 `settings.json`, API part

The values are the defaults plus an example address. `JsonSettingsStore` lays the stored values over the defaults, so a
file containing only these keys loads with defaults for every other setting.

```json
{
  "catalogSource": "BonikPosApi",
  "api": {
    "baseUrl": "https://pos.example.com/",
    "productsPath": "api/products",
    "pageSize": 500,
    "pageParameter": "page",
    "pageSizeParameter": "pageSize",
    "authScheme": "Bearer",
    "apiKeyHeader": "X-Api-Key",
    "timeoutSeconds": 30,
    "maxRetries": 2,
    "fields": {
      "itemsProperty": "items",
      "productId": "productId",
      "brandName": "brandName",
      "genericName": "genericName",
      "strength": "strength",
      "dosageForm": "dosageForm",
      "manufacturer": "manufacturer",
      "packSize": "packSize",
      "localName": "localName",
      "hasMoreProperty": "hasMore"
    }
  }
}
```

### 9.2 Response for the default field mapping

Request: `GET https://pos.example.com/api/products?page=1&pageSize=500` with `Authorization: Bearer <token>`.

```json
{
  "items": [
    {
      "productId": "P-000101",
      "brandName": "Napa",
      "genericName": "Paracetamol",
      "strength": "500 mg",
      "dosageForm": "Tablet",
      "manufacturer": "Example Pharma Ltd",
      "packSize": "10 x 10",
      "localName": "নাপা"
    },
    {
      "productId": "P-000102",
      "brandName": "Napa",
      "genericName": "Paracetamol",
      "strength": "120 mg/5 ml",
      "dosageForm": "Suspension",
      "packSize": "60 ml"
    },
    {
      "productId": 103,
      "brandName": "Seclo",
      "genericName": "Omeprazole",
      "strength": "20 mg",
      "dosageForm": "Capsule"
    }
  ],
  "hasMore": true
}
```

Result: three products; the numeric id of the third becomes `"103"`. Because `hasMore` is `true`, page 2 is requested;
the sync ends at a page with `"hasMore": false` or with no items.

### 9.3 Other field names and a top-level array

Mapping (`fields` in `settings.json`), as in the unit test
`ConfiguredFieldNames_RootArrays_NumericIds_AndCaseInsensitiveKeys_AreMapped`:

```json
{
  "itemsProperty": "",
  "productId": "sku",
  "brandName": "name",
  "genericName": "molecule",
  "strength": "potency",
  "dosageForm": "form",
  "hasMoreProperty": ""
}
```

Response:

```json
[
  { "SKU": 1001, "Name": "Napa", "molecule": "Paracetamol", "potency": "500 mg", "FORM": "Tablet" },
  { "sku": "1002", "name": "Seclo" }
]
```

Result: products `1001` and `1002`. Without `hasMore`, paging stops at the first page with fewer than `pageSize` items.

## 10. Known limitations (verified in the code)

1. *(Fixed.)* A timeout or I/O error while the body is read is now retried like a failed request and then reported
   as `CatalogUnavailableException` ("did not finish sending the catalog within N s" / "connection … was interrupted"),
   so the cached catalog is used and Settings shows the message (tests
   `BodyThatStopsArriving_…`, `ConnectionDroppedWhileReadingTheBody_…`).
2. *(Mitigated.)* A server that ignores the page parameter no longer causes 2000 requests: paging stops at the first
   page that only repeats products already received, with a warning (§5.3).
3. **Server caps the page size.** If the server returns fewer items per page than `PageSize` and sends no `hasMore`,
   the first page looks like the last one and the sync stops after it. The catalog then loads with a warning (§5.3) but
   is incomplete. Send `hasMore`, or make `PageSize` equal to the server's page size.
4. Every sync downloads the full catalog; there are no conditional or incremental requests and no background sync.
5. `PageSize`, the paging parameter names, the timeout, the retry count and the `manufacturer`, `packSize`,
   `localName` and `hasMore` field names are not on the Settings screen (§2).
6. Only top-level properties are read, for the item array and for product fields; nested paths are not supported.
7. `Aliases` are not read from the API.
8. `ProductsPath` must not contain a query string.
9. `Retry-After` is honoured only as a delay in seconds, not as an HTTP date, and is capped at 10 s.
10. The catalog cache is stored unencrypted; certificate pinning is not implemented.
11. Not tested against a real BonikPOS server.

## 11. Recommendations for the BonikPOS endpoint

Derived from the adapter's behaviour; these are not requirements the code enforces.

- Return `hasMore` as a boolean on every page (limitations 2 and 3).
- Honour the page and page-size parameters with 1-based pages and a stable sort order, so pages neither overlap nor skip
  products while a sync runs.
- Return stable product ids, as strings, that are the ids the POS uses; they are shown to the owner and stored in
  history.
- Return strength and dosage form as separate fields in the forms the parser understands (§8.1); return a Bangla name
  in the local-name field when available.
- Use 401/403 for bad credentials, 429 with `Retry-After` in seconds for throttling, and 5xx only for transient
  failures.
- Serve HTTPS with a certificate the phones trust.
