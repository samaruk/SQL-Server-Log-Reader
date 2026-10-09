using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Text;
using BonikPOS.VoiceSearch.Presentation.Services;
using BonikPOS.VoiceSearch.Presentation.ViewModels;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Presentation;

internal sealed class FakeNavigation : INavigationService
{
    public List<string> Routes { get; } = [];

    public int BackCount { get; private set; }

    public Task GoToAsync(string route)
    {
        Routes.Add(route);
        return Task.CompletedTask;
    }

    public Task GoBackAsync()
    {
        BackCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public Task AlertAsync(string title, string message, string accept = "OK") => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(ConfirmResult);
}

internal sealed class FakePermissions(PermissionStatus status) : IPermissionService
{
    public Task<PermissionStatus> CheckMicrophoneAsync() => Task.FromResult(status);

    public Task<PermissionStatus> RequestMicrophoneAsync() => Task.FromResult(status);

    public void OpenAppSettings()
    {
    }
}

internal sealed class InMemoryHistory : ISearchHistoryStore
{
    public List<SearchHistoryEntry> Entries { get; } = [];

    public Task AddAsync(SearchHistoryEntry entry, CancellationToken cancellationToken)
    {
        Entries.Insert(0, entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHistoryEntry>> GetRecentAsync(int maxEntries, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SearchHistoryEntry>>([.. Entries.Take(maxEntries)]);

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Entries.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public VoiceSearchSettings Settings { get; set; } = new();

    public Task<VoiceSearchSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task SaveAsync(VoiceSearchSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class NoProfileStore : IVoiceProfileStore
{
    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<VoiceProfile?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<VoiceProfile?>(null);

    public Task SaveAsync(VoiceProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeCatalogManager(IMedicineSearchService search) : ICatalogManager
{
    public MedicineCatalogInfo Info => search.CatalogInfo;

    public Task ReloadAsync(CancellationToken cancellationToken) => search.InitializeAsync(cancellationToken);
}

public sealed class NoMatchViewModelTests
{
    private static MedicineSearchResult NoMatch(string text, params CorrectionSuggestion[] suggestions) =>
        new(new MedicineQuery(text, QuerySource.Voice, 0.8, OwnerVoiceDecision.Owner), QueryParser.Parse(text), SearchDecision.NoMatch, [], [],
            $"No product in the catalog matches \"{text}\".", TimeSpan.Zero, MedicineCatalogInfo.NotLoaded)
        {
            Suggestions = suggestions,
        };

    [Fact]
    public void Appearing_ShowsWhatWasUnderstood_AndTheSuggestions()
    {
        var session = new SessionState { PendingResult = NoMatch("sekloo 20 mg capsule", new CorrectionSuggestion("Seclo 20 mg capsule", "Seclo", 0.82)) };
        var vm = new NoMatchViewModel(session, new FakeNavigation());

        vm.AppearingCommand.Execute(null);

        Assert.Equal("sekloo 20 mg capsule", vm.Query);
        Assert.Equal("Name: \"sekloo\"; strength: 20 mg; form: Capsule", vm.Understood);
        Assert.Contains("No product in the catalog matches", vm.Explanation);
        Assert.Equal(["Seclo 20 mg capsule"], vm.Suggestions);
        Assert.True(vm.HasSuggestions);
        Assert.Equal(3, vm.Tips.Count);
    }

    [Fact]
    public async Task PickingASuggestion_StartsANewSearchForIt_WithoutSelectingAnything()
    {
        var session = new SessionState { PendingResult = NoMatch("sekloo", new CorrectionSuggestion("Seclo", "Seclo", 0.82)) };
        var navigation = new FakeNavigation();
        var vm = new NoMatchViewModel(session, navigation);
        vm.AppearingCommand.Execute(null);

        await vm.SearchSuggestionCommand.ExecuteAsync("Seclo");

        Assert.Equal("Seclo", session.TextToEdit);
        Assert.True(session.SearchTextOnReturn);       // the search screen searches it again on return
        Assert.Null(session.PendingResult);
        Assert.Null(session.LastSelectionMessage);     // a suggestion is not a selection
        Assert.Equal(1, navigation.BackCount);
    }

    [Fact]
    public async Task EditText_And_TryAgain_ReturnWithoutSearching()
    {
        var session = new SessionState { PendingResult = NoMatch("umbrella") };
        var navigation = new FakeNavigation();
        var vm = new NoMatchViewModel(session, navigation);
        vm.AppearingCommand.Execute(null);
        Assert.False(vm.HasSuggestions);

        await vm.EditTextCommand.ExecuteAsync(null);
        Assert.Equal("umbrella", session.TextToEdit);
        Assert.False(session.SearchTextOnReturn);

        session.PendingResult = NoMatch("umbrella");
        await vm.TryAgainCommand.ExecuteAsync(null);
        Assert.Null(session.PendingResult);
        Assert.Equal(2, navigation.BackCount);

        await vm.SearchSuggestionCommand.ExecuteAsync("  ");
        Assert.Equal(2, navigation.BackCount);       // blank suggestions are ignored
    }
}

public sealed class SearchViewModelTests
{
    private sealed class Harness
    {
        public required SearchViewModel ViewModel { get; init; }

        public required FakeNavigation Navigation { get; init; }

        public required SessionState Session { get; init; }

        public required InMemoryHistory History { get; init; }

        public required MedicineSearchService Search { get; init; }
    }

    private static async Task<Harness> CreateAsync()
    {
        var search = await TestCatalogs.SearchServiceAsync();
        var runtime = new FakeRuntime();
        var navigation = new FakeNavigation();
        var session = new SessionState();
        var history = new InMemoryHistory();
        var vm = new SearchViewModel(
            new Core.Pipeline.VoiceSearchPipeline(new TestCapture([]), runtime, search, new Core.Telemetry.PerformanceTelemetry()),
            runtime, search, new FakeCatalogManager(search), new OwnerProfileService(new NoProfileStore(), runtime),
            new SettingsService(new InMemorySettingsStore()), history, session, navigation, new FakeDialogs(),
            new FakePermissions(PermissionStatus.Granted), new ImmediateDispatcher(), TimeProvider.System);
        return new Harness { ViewModel = vm, Navigation = navigation, Session = session, History = history, Search = search };
    }

    private static Task<MedicineSearchResult> SearchAsync(Harness h, string text) =>
        h.Search.SearchAsync(new MedicineQuery(text, QuerySource.Typed), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AmbiguousResult_NavigatesToTheClarificationScreen_WithThePendingResult()
    {
        var h = await CreateAsync();
        var result = await SearchAsync(h, "seclo");
        Assert.Equal(SearchDecision.Ambiguous, result.Decision);

        await h.ViewModel.ApplyResultAsync(result);

        Assert.Equal([Routes.Ambiguity], h.Navigation.Routes);
        Assert.Same(result, h.Session.PendingResult);
        Assert.Equal(SearchScreenState.Results, h.ViewModel.State);
        Assert.Equal(2, h.ViewModel.Results.Count);
        Assert.Equal("Several products match - please choose", h.ViewModel.DecisionText);
        Assert.Empty(h.History.Entries); // nothing is selected automatically
    }

    [Fact]
    public async Task NoMatchResult_NavigatesToTheNoMatchScreen()
    {
        var h = await CreateAsync();
        var result = await SearchAsync(h, "umbrella");
        Assert.Equal(SearchDecision.NoMatch, result.Decision);

        await h.ViewModel.ApplyResultAsync(result);

        Assert.Equal([Routes.NoMatch], h.Navigation.Routes);
        Assert.Same(result, h.Session.PendingResult);
        Assert.False(h.ViewModel.HasResults);
    }

    [Theory]
    [InlineData("seclo 20 mg", false)]  // Confident: stays on the search screen, still needs the owner's confirmation
    [InlineData("seclo 30 mg", true)]   // LowConfidence: stays, with a visible warning
    public async Task OtherDecisions_StayOnTheSearchScreen(string query, bool warning)
    {
        var h = await CreateAsync();
        var result = await SearchAsync(h, query);

        await h.ViewModel.ApplyResultAsync(result);

        Assert.Empty(h.Navigation.Routes);
        Assert.Null(h.Session.PendingResult);
        Assert.True(h.ViewModel.HasResults);
        Assert.True(h.ViewModel.Results[0].IsTop);
        Assert.Equal(warning, h.ViewModel.ShowLowConfidence);
        Assert.Equal(warning, h.ViewModel.Results[0].HasWarning);
        Assert.Empty(h.History.Entries);
    }

    [Fact]
    public async Task ConfirmingAResult_RecordsItInHistory_WithTheCatalogId()
    {
        var h = await CreateAsync();
        await h.ViewModel.ApplyResultAsync(await SearchAsync(h, "seclo 20 mg"));
        var top = h.ViewModel.Results[0];

        await h.ViewModel.ConfirmCommand.ExecuteAsync(top);

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(HistoryOutcome.Confirmed, entry.Outcome);
        Assert.Equal("T-SECLO-20", entry.SelectedProductId);
        Assert.Equal("Seclo 20 mg Capsule", entry.SelectedProductLabel);
        Assert.Contains("T-SECLO-20", h.ViewModel.Notice);
        Assert.False(h.ViewModel.HasResults);
    }
}

public sealed class AmbiguityViewModelTests
{
    [Fact]
    public async Task ChoosingAnOption_RecordsTheChoice_AndReturns()
    {
        var search = await TestCatalogs.SearchServiceAsync();
        var result = await search.SearchAsync(new MedicineQuery("seclo", QuerySource.Correction), TestContext.Current.CancellationToken);
        var session = new SessionState { PendingResult = result };
        var history = new InMemoryHistory();
        var navigation = new FakeNavigation();
        var vm = new AmbiguityViewModel(session, history, new SettingsService(new InMemorySettingsStore()), navigation, TimeProvider.System);

        vm.AppearingCommand.Execute(null);
        Assert.Equal(["T-SECLO-20", "T-SECLO-40"], vm.Options.Select(o => o.ProductId).Order());
        var forty = vm.Options.Single(o => o.ProductId == "T-SECLO-40");

        await vm.ChooseCommand.ExecuteAsync(forty);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(HistoryOutcome.Corrected, entry.Outcome); // the query came from a correction
        Assert.Equal("T-SECLO-40", entry.SelectedProductId);
        Assert.Equal("Selected Seclo 40 mg Capsule (ID T-SECLO-40).", session.LastSelectionMessage);
        Assert.Null(session.PendingResult);
        Assert.Equal(1, navigation.BackCount);
    }

    [Fact]
    public async Task HistoryDisabled_ChoiceIsNotRecorded()
    {
        var search = await TestCatalogs.SearchServiceAsync();
        var result = await search.SearchAsync(new MedicineQuery("seclo", QuerySource.Voice), TestContext.Current.CancellationToken);
        var session = new SessionState { PendingResult = result };
        var history = new InMemoryHistory();
        var settings = new SettingsService(new InMemorySettingsStore { Settings = new VoiceSearchSettings { HistoryEnabled = false } });
        var vm = new AmbiguityViewModel(session, history, settings, new FakeNavigation(), TimeProvider.System);
        vm.AppearingCommand.Execute(null);

        await vm.ChooseCommand.ExecuteAsync(vm.Options[0]);

        Assert.Empty(history.Entries);
        Assert.NotNull(session.LastSelectionMessage);
    }
}
