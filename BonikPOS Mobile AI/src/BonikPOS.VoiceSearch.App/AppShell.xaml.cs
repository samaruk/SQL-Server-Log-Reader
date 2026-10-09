using BonikPOS.VoiceSearch.App.Views;
using BonikPOS.VoiceSearch.Presentation.Services;

namespace BonikPOS.VoiceSearch.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Welcome, typeof(WelcomePage));
        Routing.RegisterRoute(Routes.MicrophoneTest, typeof(MicrophoneTestPage));
        Routing.RegisterRoute(Routes.EnrollmentInstructions, typeof(EnrollmentInstructionsPage));
        Routing.RegisterRoute(Routes.EnrollmentRecording, typeof(EnrollmentRecordingPage));
        Routing.RegisterRoute(Routes.EnrollmentResult, typeof(EnrollmentResultPage));
        Routing.RegisterRoute(Routes.Ambiguity, typeof(AmbiguityPage));
        Routing.RegisterRoute(Routes.NoMatch, typeof(NoMatchPage));
        Routing.RegisterRoute(Routes.VoiceSettings, typeof(VoiceSettingsPage));
        Routing.RegisterRoute(Routes.Diagnostics, typeof(DiagnosticsPage));
    }
}
