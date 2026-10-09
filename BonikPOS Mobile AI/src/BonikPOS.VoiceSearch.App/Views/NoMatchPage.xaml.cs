using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class NoMatchPage : ContentPage
{
    private readonly NoMatchViewModel _viewModel;

    public NoMatchPage(NoMatchViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.AppearingCommand.Execute(null);
    }
}
