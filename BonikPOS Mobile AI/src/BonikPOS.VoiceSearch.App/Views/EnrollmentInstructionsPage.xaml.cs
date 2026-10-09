using BonikPOS.VoiceSearch.Presentation.ViewModels;

namespace BonikPOS.VoiceSearch.App.Views;

public partial class EnrollmentInstructionsPage : ContentPage
{
    private readonly EnrollmentInstructionsViewModel _viewModel;

    public EnrollmentInstructionsPage(EnrollmentInstructionsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }
}
