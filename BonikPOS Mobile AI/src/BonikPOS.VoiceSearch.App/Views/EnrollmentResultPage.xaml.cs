using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class EnrollmentResultPage : ContentPage
{
    private readonly EnrollmentResultViewModel _viewModel;

    public EnrollmentResultPage(EnrollmentResultViewModel viewModel)
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
