using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class WelcomePage : ContentPage
{
    private readonly WelcomeViewModel _viewModel;

    public WelcomePage(WelcomeViewModel viewModel)
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
