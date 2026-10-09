using BonikPOS.VoiceSearch.Core.Enrollment;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Presentation.Services;

/// <summary>Version of the consent text in docs/PRIVACY.md that the welcome screen shows.</summary>
public static class ConsentText
{
    public const string Version = "2026-10-v1";

    public const string Summary =
        "BonikPOS Voice Search records a few short samples of your voice to create a voice profile. The profile is a list " +
        "of numbers (a speaker embedding) derived from your voice. It is encrypted and stored only on this phone, and " +
        "it is used only to prefer your voice when you search for medicines. Raw recordings are not kept. Audio is processed " +
        "on the phone and is not uploaded. You can delete the profile at any time in Settings > Owner voice. The voice " +
        "profile is not a password: it does not prove who is speaking and must not be used to authorise anything.";
}

/// <summary>State that spans several screens of the onboarding and search flows.</summary>
public sealed class SessionState
{
    public ConsentRecord? Consent { get; set; }

    public VoiceProfile? EnrollmentDraft { get; set; }

    public (double Mean, double Min, double StdDev, double SuggestedThreshold)? EnrollmentCalibration { get; set; }

    public IReadOnlyList<EnrollmentSampleResult> EnrollmentSamples { get; set; } = [];

    /// <summary>The last search result that needs a decision on the ambiguity or no-match screen.</summary>
    public MedicineSearchResult? PendingResult { get; set; }

    public SpeechRecognitionResult? PendingRecognition { get; set; }

    public OwnerVerificationResult? PendingVerification { get; set; }

    /// <summary>Set by the clarification screens so the search screen can show what was chosen.</summary>
    public string? LastSelectionMessage { get; set; }

    /// <summary>Text to put back into the editor (no-match screen "edit text").</summary>
    public string? TextToEdit { get; set; }

    /// <summary>When true the search screen searches <see cref="TextToEdit"/> as soon as it reappears.</summary>
    public bool SearchTextOnReturn { get; set; }

    public void ClearPending()
    {
        PendingResult = null;
        PendingRecognition = null;
        PendingVerification = null;
    }
}
