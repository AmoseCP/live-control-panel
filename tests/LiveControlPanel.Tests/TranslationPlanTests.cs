using LiveControlPanel.Config;
using LiveControlPanel.Core;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Who gets a second broadcast, and in what language.
///
/// This decision is read by both the state manager and the orchestrator, so it is pinned on its own:
/// the two must never disagree, or the panel promises a bilingual service and then produces one
/// broadcast — with nobody around to explain the difference.
/// </summary>
public sealed class TranslationPlanTests
{
    private static AppSettings Settings(
        bool enabled = true,
        string target = "en",
        string apiKey = "key",
        string streamId = "stream-2",
        string suffix = " (English)")
    {
        var settings = new AppSettings();
        settings.Translation.Enabled = enabled;
        settings.Translation.TargetLanguage = target;
        settings.Translation.ApiKey = apiKey;
        settings.Translation.StreamId = streamId;
        settings.Translation.TitleSuffix = suffix;
        return settings;
    }

    private static ServiceTemplate Template(bool translate = true, string? target = null) =>
        new() { Id = "t", Name = "Service", Translate = translate, TargetLanguage = target };

    [Fact]
    public void The_master_switch_decides_whether_any_service_translates()
    {
        Assert.False(TranslationPlan.For(Settings(enabled: false), Template()).Active);
        Assert.True(TranslationPlan.For(Settings(), Template()).Active);
    }

    [Fact]
    public void A_service_can_opt_out_individually() =>
        Assert.False(TranslationPlan.For(Settings(), Template(translate: false)).Active);

    /// <summary>
    /// Existing templates predate this field, so they deserialize with its default. Switching
    /// translation on has to cover the whole schedule rather than silently nothing.
    /// </summary>
    [Fact]
    public void A_template_that_predates_the_field_is_included_by_default()
    {
        var template = new ServiceTemplate { Id = "old", Name = "Old" };

        Assert.True(template.Translate);
        Assert.True(TranslationPlan.For(Settings(), template).Active);
    }

    [Fact]
    public void An_ad_hoc_service_with_no_template_follows_the_global_setting()
    {
        Assert.True(TranslationPlan.For(Settings(), null).Active);
        Assert.False(TranslationPlan.For(Settings(enabled: false), null).Active);
    }

    /// <summary>
    /// The mixed-schedule case: a Chinese service produces English, an English one produces Chinese,
    /// from the same panel and the same camera.
    /// </summary>
    [Fact]
    public void A_service_overrides_the_target_language()
    {
        Assert.Equal("zh-CN", TranslationPlan.For(Settings(), Template(target: "zh-CN")).TargetLanguage);
        Assert.Equal("en", TranslationPlan.For(Settings(), Template()).TargetLanguage);
    }

    [Fact]
    public void A_blank_target_language_anywhere_falls_back_to_english()
    {
        Assert.Equal("en", TranslationPlan.For(Settings(target: "  "), Template(target: "  ")).TargetLanguage);
        Assert.Equal("en", TranslationPlan.For(Settings(target: ""), null).TargetLanguage);
    }

    /// <summary>
    /// Ready is separate from active on purpose: without its own stream key the second broadcast
    /// could never be bound to anything, so creating it would only leave a leftover for the next
    /// operator to clean up.
    /// </summary>
    [Fact]
    public void Ready_requires_both_a_stream_key_and_an_api_key()
    {
        Assert.True(TranslationPlan.For(Settings(), Template()).Ready);
        Assert.False(TranslationPlan.For(Settings(streamId: ""), Template()).Ready);
        Assert.False(TranslationPlan.For(Settings(apiKey: ""), Template()).Ready);
    }

    [Fact]
    public void An_inactive_plan_is_never_ready() =>
        Assert.False(TranslationPlan.For(Settings(enabled: false), Template()).Ready);

    [Fact]
    public void The_translated_title_is_the_primary_one_plus_the_suffix() =>
        Assert.Equal("8/5/2026 Service (English)",
            TranslationPlan.For(Settings(), Template()).TitleFor("8/5/2026 Service"));

    /// <summary>
    /// The two titles must differ: adoption of a lost create attempt matches on exact title, so an
    /// empty suffix would let the translated broadcast adopt the primary one.
    /// </summary>
    [Fact]
    public void A_blank_suffix_still_produces_a_distinct_title()
    {
        var plan = TranslationPlan.For(Settings(suffix: ""), Template());

        Assert.NotEqual("8/5/2026 Service", plan.TitleFor("8/5/2026 Service"));
    }
}
