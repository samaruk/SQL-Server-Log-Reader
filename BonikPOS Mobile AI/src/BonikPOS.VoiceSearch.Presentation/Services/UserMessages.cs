using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Enrollment;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Presentation.Services;

/// <summary>User-facing wording, kept in one place for later localisation.</summary>
public static class UserMessages
{
    public static string Describe(EnrollmentIssue issue) => issue switch
    {
        EnrollmentIssue.NoSpeech => "No speech was heard. Hold the phone at your usual distance and read the sentence.",
        EnrollmentIssue.TooShort => "The recording was too short. Please read the whole sentence.",
        EnrollmentIssue.TooQuiet => "Your voice was too quiet. Speak a little louder or move closer.",
        EnrollmentIssue.TooLoud => "Your voice was too loud. Move the phone a little further away.",
        EnrollmentIssue.Clipping => "The sound was distorted (too loud). Move the phone a little further away.",
        EnrollmentIssue.Noisy => "There is too much background noise. Try again when it is quieter.",
        EnrollmentIssue.PossibleSecondSpeaker => "Another voice may be in the recording. Record again when nobody else is talking.",
        EnrollmentIssue.InconsistentWithOtherSamples => "This sample sounds different from your other samples. Please record it again in your normal voice.",
        EnrollmentIssue.SpeakerModelUnavailable => "The speaker model is not installed, so enrollment cannot continue.",
        _ => issue.ToString(),
    };

    public static string Describe(RecordingIssue issue) => issue switch
    {
        RecordingIssue.NoSpeech => "No speech detected",
        RecordingIssue.TooShort => "Speech too short",
        RecordingIssue.TooQuiet => "Too quiet",
        RecordingIssue.TooLoud => "Too loud",
        RecordingIssue.Clipping => "Distorted (clipping)",
        RecordingIssue.Noisy => "Too much background noise",
        _ => issue.ToString(),
    };

    public static string Describe(TargetSpeakerExtractorStatus? status) => status?.Availability switch
    {
        TargetSpeakerExtractionAvailability.Active => "Owner-voice extraction ON",
        TargetSpeakerExtractionAvailability.NeedsReEnrollment => "Owner-voice extraction needs re-enrollment",
        TargetSpeakerExtractionAvailability.IncompatibleModel => "Owner-voice extraction: model incompatible",
        TargetSpeakerExtractionAvailability.Failed => "Owner-voice extraction failed (off)",
        TargetSpeakerExtractionAvailability.Disabled => "Owner-voice extraction disabled",
        _ => "Owner-voice extraction not installed - other voices are NOT removed",
    };

    public static string Describe(OwnerVerificationResult? v) => v?.Decision switch
    {
        OwnerVoiceDecision.Owner => $"Owner voice confirmed ({v.Similarity:0.00})",
        OwnerVoiceDecision.Uncertain => v.OtherVoiceSuspected ? "Another voice may be mixed in - please check" : "Owner voice uncertain - please check",
        OwnerVoiceDecision.NotOwner => "Not the owner's voice",
        OwnerVoiceDecision.InsufficientSpeech => "Too little speech to check the voice",
        OwnerVoiceDecision.NoProfile => "Owner voice not enrolled",
        OwnerVoiceDecision.ModelUnavailable => "Owner voice check unavailable",
        _ => string.Empty,
    };

    public static string Describe(SearchDecision decision) => decision switch
    {
        SearchDecision.Confident => "Best match - please confirm",
        SearchDecision.NeedsConfirmation => "Please confirm the product",
        SearchDecision.Ambiguous => "Several products match - please choose",
        SearchDecision.LowConfidence => "Not sure - please check carefully",
        _ => "No matching product",
    };

    public static string Describe(MatchKind kind) => kind switch
    {
        MatchKind.Exact => "Exact name",
        MatchKind.ExactPhonetic => "Same pronunciation",
        MatchKind.Prefix => "Partial name",
        MatchKind.Fuzzy => "Similar name",
        _ => string.Empty,
    };
}
