using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class EnrollmentRecordingPage : ContentPage
{
    private readonly EnrollmentViewModel _viewModel;

    public EnrollmentRecordingPage(EnrollmentViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.AppearingCommand.Execute(null);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_viewModel.StopRecordingCommand.CanExecute(null))
        {
            _viewModel.StopRecordingCommand.Execute(null); // never keep the microphone open behind another screen
        }
    }
}
