using LiveControlPanel.Config;

namespace LiveControlPanel.Core;

/// <summary>
/// Whether this service gets the AI-translated second broadcast, and in what language.
///
/// It lives on its own because two places must agree on the answer and must never drift: the state
/// manager, which tells the operator page before anything starts, and the orchestrator, which acts
/// on it. A service that shows "bilingual" on the panel and then creates one broadcast is exactly
/// the kind of surprise there is nobody to explain at 04:40.
/// </summary>
public sealed record TranslationPlan(bool Active, bool Ready, string TargetLanguage, string TitleSuffix)
{
    public static readonly TranslationPlan Off = new(false, false, "en", "");

    public static TranslationPlan For(AppSettings settings, ServiceTemplate? template)
    {
        var translation = settings.Translation;

        // A service opts out individually; the master switch decides whether any of them run at all.
        var active = translation.Enabled && (template?.Translate ?? true);
        if (!active) return Off;

        var target = Pick(template?.TargetLanguage, Pick(translation.TargetLanguage, "en"));

        // Ready means the second broadcast can actually be created and bound. Without its own stream
        // key it could never be bound to anything, so creating it would only leave a leftover for the
        // next operator to clean up.
        var ready = !string.IsNullOrWhiteSpace(translation.StreamId)
                    && !string.IsNullOrWhiteSpace(translation.ApiKey);

        return new TranslationPlan(true, ready, target, translation.TitleSuffix ?? "");
    }

    /// <summary>Title of the translated broadcast, given the primary one.</summary>
    public string TitleFor(string primaryTitle) =>
        string.IsNullOrEmpty(TitleSuffix) ? primaryTitle + " (translated)" : primaryTitle + TitleSuffix;

    private static string Pick(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
