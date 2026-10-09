namespace BonikPOS.VoiceSearch.Core.Enrollment;

/// <summary>A phrase the owner reads during enrollment.</summary>
/// <param name="Language">"bn", "en" or "bn-en" (Banglish: Bangla with English medicine names).</param>
public sealed record EnrollmentPrompt(string Id, string Text, string Language, string Instruction);

/// <summary>
/// Default enrollment phrases. They mix the languages used at the counter so that the profile covers the owner's
/// real speaking style. The final prompt contains random digits that the owner reads aloud; it discourages
/// enrolling from a pre-recorded clip, but it is not liveness detection and not a security control.
/// </summary>
public static class EnrollmentPrompts
{
    public static IReadOnlyList<EnrollmentPrompt> Default { get; } =
    [
        new("bn-intro", "আমি এই দোকানের মালিক। ঔষধ খোঁজার জন্য আমি আমার কণ্ঠ ব্যবহার করছি।", "bn",
            "Read this sentence in your normal speaking voice."),
        new("en-napa", "Napa Extra five hundred milligram tablet, two strips please.", "en",
            "Say it the way you would ask for it at the counter."),
        new("mix-seclo", "সেকলো বিশ মিলিগ্রাম ক্যাপসুল, আর Maxpro twenty দিন।", "bn-en",
            "Mix Bangla and English naturally, as you usually do."),
        new("en-fexo", "Please check if we have Fexo one twenty and Monas ten in stock.", "en",
            "Speak at your normal distance from the phone."),
        new("bn-syrup", "প্যারাসিটামল সিরাপ আর অ্যান্টাসিড সাসপেনশন কোন তাকে আছে দেখুন।", "bn",
            "Read at your usual pace."),
        new("mix-azith", "Zimax পাঁচশো মিলিগ্রাম ট্যাবলেট আর Alatrol সিরাপ লাগবে।", "bn-en",
            "Use the brand names exactly as you say them in the shop."),
    ];

    /// <summary>A short phrase with random digits (owner reads them in Bangla or English).</summary>
    public static EnrollmentPrompt CreateSpokenCodePrompt(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        string digits = string.Concat(Enumerable.Range(0, 4).Select(_ => random.Next(0, 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string bangla = string.Concat(digits.Select(c => (char)('০' + (c - '0'))));
        return new EnrollmentPrompt("spoken-code", $"আজকের কোড {bangla} — today's code is {string.Join(' ', digits.ToCharArray())}", "bn-en",
            "Read the code aloud. It changes every time.");
    }
}
