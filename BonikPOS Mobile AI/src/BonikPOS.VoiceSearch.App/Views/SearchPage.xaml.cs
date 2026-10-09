using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class SearchPage : ContentPage
{
    private readonly SearchViewModel _viewModel;

    public SearchPage(SearchViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        if (DeviceInfo.Idiom == DeviceIdiom.Tablet || DeviceInfo.Idiom == DeviceIdiom.Desktop)
        {
            // Wide screens (counter tablets): two result cards per row.
            ResultsView.ItemsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical) { VerticalItemSpacing = 10 };
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.AppearingCommand.Execute(null);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.DisappearingCommand.Execute(null); // stop listening when the screen is left
    }
}
