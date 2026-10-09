using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.AI.Speakers;

internal static class SpeakerCalibrationOptions
{
    public static SpeakerCalibration Read(ModelOptions o)
    {
        var d = new SpeakerCalibration();
        return new SpeakerCalibration
        {
            DefaultAcceptThreshold = o.Double("default_accept", d.DefaultAcceptThreshold),
            MinAcceptThreshold = o.Double("min_accept", d.MinAcceptThreshold),
            MaxAcceptThreshold = o.Double("max_accept", d.MaxAcceptThreshold),
            EnrollmentMinConsistency = o.Double("enroll_min_consistency", d.EnrollmentMinConsistency),
            SingleSpeakerMinWindow = o.Double("single_speaker_min_window", d.SingleSpeakerMinWindow),
        };
    }
}
