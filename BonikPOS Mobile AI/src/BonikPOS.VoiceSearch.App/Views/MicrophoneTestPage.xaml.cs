using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class MicrophoneTestPage : ContentPage
{
    private readonly MicrophoneTestViewModel _viewModel;

    public MicrophoneTestPage(MicrophoneTestViewModel viewModel)
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
