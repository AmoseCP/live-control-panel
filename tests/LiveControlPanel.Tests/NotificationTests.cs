using LiveControlPanel.Core;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// FR 5.4 and the plan's T-23/T-24/T-25. Every stream is unlisted, so this message is how anyone
/// finds the link — duplicate sends and silent failures are both unacceptable.
/// </summary>
public class NotificationTests
{
    [Fact]
    public async Task Sending_delivers_the_title_and_the_watch_url()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        var result = await host.Notifications.SendCurrentAsync();

        Assert.True(result.Ok);
        var message = Assert.Single(host.Telegram.Sent);
        Assert.Contains("8/5/2026 Wednesday Service", message);
        Assert.Contains("https://www.youtube.com/live/bcast1", message);
    }

    // ---------------------------------------------------------------- T-24 idempotency

    [Fact]
    public async Task Five_taps_put_exactly_one_message_in_the_group()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        for (var i = 0; i < 5; i++) await host.Notifications.SendCurrentAsync();

        Assert.Single(host.Telegram.Sent);
    }

    [Fact]
    public async Task Five_concurrent_taps_put_exactly_one_message_in_the_group()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => host.Notifications.SendCurrentAsync()));

        Assert.Single(host.Telegram.Sent);
    }

    [Fact]
    public async Task A_repeat_tap_reports_when_the_message_was_already_sent()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        await host.Notifications.SendCurrentAsync();
        var second = await host.Notifications.SendCurrentAsync();

        Assert.True(second.Ok);
        Assert.Contains("已在", second.Message);
        Assert.NotNull(host.State.Snapshot().Telegram.SentAt);
    }

    // ---------------------------------------------------------------- T-25 failure is visible

    [Fact]
    public async Task A_failure_is_reported_and_leaves_the_send_retryable()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();
        host.Telegram.ShouldFail = true;

        var failed = await host.Notifications.SendCurrentAsync();

        Assert.False(failed.Ok);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));

        var state = host.State.Snapshot();
        Assert.Null(state.Telegram.SentAt);
        Assert.Equal(failed.Message, state.Telegram.LastError);

        // Retry after the group permission is restored.
        host.Telegram.ShouldFail = false;
        var retried = await host.Notifications.SendCurrentAsync();

        Assert.True(retried.Ok);
        Assert.Single(host.Telegram.Sent);
        Assert.Null(host.State.Snapshot().Telegram.LastError);
    }

    [Fact]
    public async Task Sending_before_a_broadcast_exists_explains_why_it_cannot()
    {
        using var host = new TestHost();

        var result = await host.Notifications.SendCurrentAsync();

        Assert.False(result.Ok);
        Assert.Contains("还没有创建直播", result.Message);
        Assert.Empty(host.Telegram.Sent);
    }

    [Fact]
    public async Task A_template_specific_message_overrides_the_default()
    {
        using var host = new TestHost();

        var templates = host.Config.Templates.Select(t => t.Clone()).ToList();
        templates.Single(t => t.Id == "wednesday-service").TelegramMessage = "今晚聚会：{title}\n{url}";
        host.Config.SaveTemplates(templates);

        host.SetToday();
        await host.Orchestrator.StartTodayAsync();
        await host.Notifications.SendCurrentAsync();

        Assert.StartsWith("今晚聚会：", Assert.Single(host.Telegram.Sent));
    }

    // ---------------------------------------------------------------- template rendering

    [Theory]
    [InlineData("{title}\n{url}", "T", "U", "T\nU")]
    [InlineData("{title} — {url}", "T", "U", "T — U")]
    [InlineData("no placeholders", "T", "U", "no placeholders")]
    [InlineData("{url}", "T", "U", "U")]
    public void Rendering_substitutes_both_placeholders(
        string pattern, string title, string url, string expected)
    {
        Assert.Equal(expected, NotificationService.Render(pattern, title, url));
    }
}
