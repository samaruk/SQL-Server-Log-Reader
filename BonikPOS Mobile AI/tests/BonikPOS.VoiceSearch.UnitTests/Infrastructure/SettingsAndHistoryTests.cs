using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Infrastructure.Security;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Infrastructure;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Settings_RoundTripEveryNestedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new JsonSettingsStore(_dir.AppPaths());
        var settings = new VoiceSearchSettings
        {
            LanguageMode = RecognitionLanguageMode.Bangla,
            Search = new SearchThresholds { ConfidentScore = 0.93, AmbiguityMargin = 0.04, MaxCandidates = 7 },
            Verification = new OwnerVerificationSettings { RequireOwnerMatch = false, AcceptThresholdOverride = 0.77, UncertainBand = 0.05 },
            Pipeline = new PipelineSettings { Mode = ListeningMode.Continuous, EndSilenceMilliseconds = 650, QueueCapacityFrames = 200 },
            AudioInput = AudioInputPreference.Unprocessed,
            Accelerator = AcceleratorPreference.CpuOnly,
            InferenceThreads = 2,
            TargetSpeakerExtractionEnabled = false,
            HistoryEnabled = false,
            CatalogSource = CatalogSourceKind.BonikPosApi,
            LocalCatalogPath = "/data/catalog.json",
            Api = new BonikPosApiSettings
            {
                BaseUrl = "https://pos.example.test/",
                PageSize = 250,
                AuthScheme = "ApiKey",
                Fields = new ProductFieldMapping { ItemsProperty = "data", BrandName = "name" },
            },
        };

        await store.SaveAsync(settings, ct);
        var loaded = await new JsonSettingsStore(_dir.AppPaths()).LoadAsync(ct);

        Assert.Equal(settings, loaded); // records all the way down: value equality covers every nested setting
        Assert.NotSame(settings.Api.Fields, loaded.Api.Fields);

        var json = await File.ReadAllTextAsync(Path.Combine(_dir.Path, "data", JsonSettingsStore.FileName), ct);
        Assert.Contains("\"languageMode\": \"Bangla\"", json); // enums are stored by name, keys in camelCase
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase); // secrets live in the secret store only
    }

    [Fact]
    public async Task SettingsFileFromAnOlderVersion_KeepsTheDefaultsOfEveryMissingValue()
    {
        // Written before most settings existed. Missing values must take their declared defaults - in particular the
        // owner-voice requirement must stay on, and nested settings must never be null.
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_dir.Path, "data"));
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, "data", JsonSettingsStore.FileName),
            """{"languageMode": "English", "verification": {"acceptThresholdOverride": 0.7}, "api": {"baseUrl": "https://pos.example.test/"}}""", ct);

        var loaded = await new JsonSettingsStore(_dir.AppPaths()).LoadAsync(ct);

        var defaults = new VoiceSearchSettings();
        Assert.Equal(RecognitionLanguageMode.English, loaded.LanguageMode);
        Assert.Equal(0.7, loaded.Verification.AcceptThresholdOverride);
        Assert.True(loaded.Verification.RequireOwnerMatch);
        Assert.Equal(defaults.Verification.UncertainBand, loaded.Verification.UncertainBand);
        Assert.Equal(defaults.Verification.WindowDrop, loaded.Verification.WindowDrop);
        Assert.Equal(defaults.Search, loaded.Search);
        Assert.Equal(defaults.Pipeline, loaded.Pipeline);
        Assert.Equal(defaults.Api with { BaseUrl = "https://pos.example.test/" }, loaded.Api);
        Assert.Equal(defaults.Api.Fields, loaded.Api.Fields);
        Assert.True(loaded.HistoryEnabled);
        Assert.True(loaded.TargetSpeakerExtractionEnabled);
        Assert.Equal(defaults.CatalogSource, loaded.CatalogSource);
    }

    [Fact]
    public async Task MissingOrCorruptSettings_FallBackToDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new JsonSettingsStore(_dir.AppPaths());
        Assert.Equal(new VoiceSearchSettings().LanguageMode, (await store.LoadAsync(ct)).LanguageMode);

        Directory.CreateDirectory(Path.Combine(_dir.Path, "data"));
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, "data", JsonSettingsStore.FileName), "{ broken", ct);
        var loaded = await store.LoadAsync(ct);
        Assert.True(loaded.Verification.RequireOwnerMatch);
        Assert.Equal(CatalogSourceKind.Demo, loaded.CatalogSource);
    }

    [Fact]
    public async Task SettingsService_CachesAndNotifiesOnUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new SettingsService(new JsonSettingsStore(_dir.AppPaths()));
        VoiceSearchSettings? notified = null;
        service.Changed += (_, s) => notified = s;

        var updated = await service.UpdateAsync(s => s with { HistoryEnabled = false }, ct);

        Assert.False(updated.HistoryEnabled);
        Assert.Same(updated, notified);
        Assert.Same(updated, service.Current);
        Assert.False((await new SettingsService(new JsonSettingsStore(_dir.AppPaths())).LoadAsync(ct)).HistoryEnabled);
    }
}

public sealed class EncryptedSearchHistoryStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSecretStore _secrets = new();
    private int _counter;

    public void Dispose() => _dir.Dispose();

    private EncryptedSearchHistoryStore NewStore() => new(_dir.AppPaths(), _secrets);

    private SearchHistoryEntry Entry(string query, HistoryOutcome outcome, string? productId) => new(
        Guid.NewGuid(), new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero).AddMinutes(_counter++), query, QuerySource.Voice,
        SearchDecision.Confident, outcome, productId, productId is null ? null : "label " + productId, 0.9, OwnerVoiceDecision.Owner);

    [Fact]
    public async Task Entries_AreStoredEncrypted_NewestFirst_AndSurviveARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await store.AddAsync(Entry("napa extra", HistoryOutcome.Confirmed, "P1"), ct);
        await store.AddAsync(Entry("seclo 20", HistoryOutcome.Dismissed, null), ct);

        var recent = await NewStore().GetRecentAsync(10, ct);

        Assert.Equal(["seclo 20", "napa extra"], recent.Select(e => e.QueryText));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "data", EncryptedSearchHistoryStore.FileName), ct);
        Assert.True(bytes.AsSpan().IndexOf("napa extra"u8) < 0, "history must not be stored in clear text");
        Assert.Single(await store.GetRecentAsync(1, ct));
    }

    [Fact]
    public async Task History_KeepsAtMostMaxEntries_DroppingTheOldest()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed the file with MaxEntries - 1 entries directly (same key and purpose as the store) to keep the test fast.
        var seeded = Enumerable.Range(0, EncryptedSearchHistoryStore.MaxEntries - 1)
            .Select(i => Entry($"old query {i}", HistoryOutcome.NoMatch, null)).Reverse().ToList();
        var protector = new AesGcmDataProtector(_secrets, SecretKeys.HistoryDataKey, "bonikpos.search-history.v1");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(seeded, InfrastructureJsonContext.Default.ListSearchHistoryEntry);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "data"));
        await File.WriteAllBytesAsync(Path.Combine(_dir.Path, "data", EncryptedSearchHistoryStore.FileName), await protector.ProtectAsync(plaintext), ct);

        var store = NewStore();
        Assert.Equal(EncryptedSearchHistoryStore.MaxEntries - 1, (await store.GetRecentAsync(int.MaxValue, ct)).Count);
        await store.AddAsync(Entry("new 1", HistoryOutcome.Confirmed, "P1"), ct);
        await store.AddAsync(Entry("new 2", HistoryOutcome.Confirmed, "P2"), ct);
        await store.AddAsync(Entry("new 3", HistoryOutcome.Confirmed, "P3"), ct);

        var all = await NewStore().GetRecentAsync(int.MaxValue, ct);
        Assert.Equal(EncryptedSearchHistoryStore.MaxEntries, all.Count);
        Assert.Equal(["new 3", "new 2", "new 1"], all.Take(3).Select(e => e.QueryText));
        Assert.DoesNotContain(all, e => e.QueryText is "old query 0" or "old query 1");   // the two oldest were dropped
        Assert.Equal("old query 2", all[^1].QueryText);
    }

    [Fact]
    public async Task Hints_ComeOnlyFromConfirmedOrCorrectedSelections()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await store.AddAsync(Entry("Napa Extra", HistoryOutcome.Confirmed, "P-EXTRA"), ct);
        await store.AddAsync(Entry("napa  extra!", HistoryOutcome.Corrected, "P-EXTRA"), ct);   // same normalised text
        await store.AddAsync(Entry("napa extra", HistoryOutcome.Corrected, "P-EXTEND"), ct);
        await store.AddAsync(Entry("seclo", HistoryOutcome.Dismissed, "P-SECLO"), ct);         // dismissed: no hint
        await store.AddAsync(Entry("fexo", HistoryOutcome.NoMatch, "P-FEXO"), ct);             // no match: no hint
        await store.AddAsync(Entry("monas", HistoryOutcome.Confirmed, null), ct);              // nothing selected: no hint

        Assert.Equal(2, store.GetPopularity("P-EXTRA"));
        Assert.Equal(1, store.GetPopularity("P-EXTEND"));
        Assert.Equal(0, store.GetPopularity("P-SECLO"));
        Assert.Equal(0, store.GetPopularity("P-FEXO"));
        Assert.Equal(["P-EXTEND", "P-EXTRA"], store.GetLearnedProducts("napaextra").Order());
        Assert.Empty(store.GetLearnedProducts("seclo"));
        Assert.Empty(store.GetLearnedProducts("fexo"));
        Assert.Empty(store.GetLearnedProducts("monas"));

        // Hints are rebuilt from disk after a restart, once history is loaded.
        var restarted = NewStore();
        Assert.Equal(0, restarted.GetPopularity("P-EXTRA"));
        await restarted.WarmUpAsync(ct);
        Assert.Equal(2, restarted.GetPopularity("P-EXTRA"));
    }

    [Fact]
    public async Task Clear_DeletesTheFileTheKeyAndTheHints()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await store.AddAsync(Entry("napa", HistoryOutcome.Confirmed, "P1"), ct);

        await store.ClearAsync(ct);

        Assert.False(File.Exists(Path.Combine(_dir.Path, "data", EncryptedSearchHistoryStore.FileName)));
        Assert.Contains(SecretKeys.HistoryDataKey, _secrets.Removed);
        Assert.Equal(0, store.GetPopularity("P1"));
        Assert.Empty(await store.GetRecentAsync(10, ct));
    }

    [Fact]
    public async Task UnreadableHistory_StartsFreshInsteadOfBlockingSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_dir.Path, "data"));
        await File.WriteAllBytesAsync(Path.Combine(_dir.Path, "data", EncryptedSearchHistoryStore.FileName), [1, 2, 3, 4], ct);
        var store = NewStore();

        Assert.Empty(await store.GetRecentAsync(10, ct));
        await store.AddAsync(Entry("napa", HistoryOutcome.Confirmed, "P1"), ct);
        Assert.Single(await NewStore().GetRecentAsync(10, ct));
    }
}
