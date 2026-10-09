using BonikPOS.VoiceSearch.Core.Pipeline;

namespace BonikPOS.VoiceSearch.App;

public partial class App : Application
{
    private readonly IVoiceSearchPipeline _pipeline;

    public App(IVoiceSearchPipeline pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell()) { Title = "BonikPOS Voice Search" };

        // Never keep the microphone open in the background.
        window.Deactivated += async (_, _) => await _pipeline.StopAsync(CancellationToken.None);
        window.Stopped += async (_, _) => await _pipeline.StopAsync(CancellationToken.None);
        return window;
    }
}
