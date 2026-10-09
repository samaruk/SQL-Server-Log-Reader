using CommunityToolkit.Mvvm.ComponentModel;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Runs an operation with busy/error handling so commands never crash the UI.</summary>
    protected async Task RunAsync(Func<Task> operation, string failurePrefix)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{failurePrefix}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Maps a dBFS level to 0..1 for level meters (-60 dBFS and below = 0).</summary>
    public static double LevelFraction(double dbfs) => Math.Clamp((dbfs + 60) / 60, 0, 1);
}
