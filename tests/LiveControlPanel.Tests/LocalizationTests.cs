using System.Net;
using System.Text.Json;
using Google;
using Google.Apis.Requests;
using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Obs;
using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// A half-translated panel is worse than an untranslated one: the operator cannot tell whether the
/// missing half is a bug or just something the panel does not know how to say. These tests walk the
/// messages the panel can actually produce and insist both languages are filled in.
/// </summary>
public class LocalizationTests
{
    private static void AssertBothLanguages(Msg? message, string where)
    {
        Assert.NotNull(message);
        Assert.False(string.IsNullOrWhiteSpace(message!.Zh), $"{where}: Chinese missing");
        Assert.False(string.IsNullOrWhiteSpace(message.En), $"{where}: English missing");
        Assert.NotEqual(message.Zh, message.En);
    }

    /// <summary>English text must actually be English — a copied Chinese string would pass a null check.</summary>
    private static void AssertEnglishHasNoChinese(Msg message, string where)
    {
        foreach (var c in message.En)
        {
            Assert.False(c >= 0x4E00 && c <= 0x9FFF,
                $"{where}: English side contains Chinese characters: {message.En}");
        }
    }

    // ---------------------------------------------------------------- preflight

    [Fact]
    public async Task Every_preflight_message_is_available_in_both_languages()
    {
        // Drive each check down its failing branch, which is where the long self-help wording lives.
        using var host = new TestHost();
        host.Obs.Connected = false;
        host.Obs.Inputs = new List<string>();
        host.Config.UpdateSettings(s => s.Obs.VideoSourceNames = new List<string> { "采集卡" });
        host.YouTube.Auth = new AuthInfo(false, null, null,
            new Msg("授权已失效。", "Authorization has expired."));
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new("old1", "8/5/2026 Morning Service", "live", "url"),
        };

        foreach (var item in await host.Preflight.RunAsync())
        {
            AssertBothLanguages(item.Message, "preflight/" + item.Key);
            AssertEnglishHasNoChinese(item.Message, "preflight/" + item.Key);
        }
    }

    [Fact]
    public async Task Every_passing_preflight_message_is_also_bilingual()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.Obs.VideoSourceNames = new List<string> { "cam" });
        host.Obs.SourceActive["cam"] = true;

        foreach (var item in await host.Preflight.RunAsync())
        {
            AssertBothLanguages(item.Message, "preflight-ok/" + item.Key);
            AssertEnglishHasNoChinese(item.Message, "preflight-ok/" + item.Key);
        }
    }

    // ---------------------------------------------------------------- orchestration

    [Fact]
    public async Task Every_orchestration_step_name_and_note_is_bilingual()
    {
        using var host = new TestHost();
        host.WithThumbnail();
        host.SetToday();
        host.Obs.CurrentScene = "PPT";

        var outcome = await host.Orchestrator.StartTodayAsync();
        AssertBothLanguages(outcome.Message, "start outcome");

        foreach (var step in host.State.Snapshot().Steps)
        {
            AssertBothLanguages(step.Name, $"step {step.Step} name");
            AssertEnglishHasNoChinese(step.Name, $"step {step.Step} name");
            if (step.Message is not null) AssertBothLanguages(step.Message, $"step {step.Step} note");
        }
    }

    [Fact]
    public async Task A_failed_step_reports_its_reason_in_both_languages()
    {
        using var host = new TestHost();
        host.SetToday();
        host.Obs.FailOnce[nameof(FakeObsClient.StartStreamAsync)] = new ObsUnavailableException();

        var outcome = await host.Orchestrator.StartTodayAsync();

        AssertBothLanguages(outcome.Message, "failed outcome");
        AssertEnglishHasNoChinese(outcome.Message, "failed outcome");

        var failed = host.State.Snapshot().Steps.Single(s => s.Status == "failed");
        AssertBothLanguages(failed.Message, "failed step note");
    }

    [Fact]
    public async Task Missing_stream_key_and_missing_schedule_are_bilingual()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.StreamId = "");

        // No schedule at all.
        AssertBothLanguages((await host.Orchestrator.StartTodayAsync()).Message, "no schedule");

        host.SetToday();
        AssertBothLanguages((await host.Orchestrator.StartTodayAsync()).Message, "no stream key");
    }

    [Fact]
    public async Task Stop_and_end_previous_outcomes_are_bilingual()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        AssertBothLanguages((await host.Orchestrator.StopAsync()).Message, "stop");
        AssertBothLanguages((await host.Orchestrator.EndPreviousAsync()).Message, "end previous");
    }

    [Fact]
    public async Task The_last_action_label_is_bilingual()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        var action = host.State.Snapshot().LastAction;
        Assert.NotNull(action);
        AssertBothLanguages(action!.What, "last action");
        AssertEnglishHasNoChinese(action.What, "last action");
    }

    // ---------------------------------------------------------------- errors

    public static IEnumerable<object[]> ErrorCases()
    {
        yield return new object[] { new NotAuthorizedException() };
        yield return new object[] { new ObsUnavailableException() };
        yield return new object[] { new ObsRequestException("no such scene") };
        yield return new object[] { new HttpRequestException("reset") };
        yield return new object[] { new TaskCanceledException() };
        yield return new object[] { new TimeoutException() };
        yield return new object[] { new Exception("something odd") };
        yield return new object[] { GoogleError("quotaExceeded", HttpStatusCode.Forbidden) };
        yield return new object[] { GoogleError("liveStreamingNotEnabled", HttpStatusCode.Forbidden) };
        yield return new object[] { GoogleError("invalidThumbnailImage", HttpStatusCode.BadRequest) };
        yield return new object[] { GoogleError("errorStreamInactive", HttpStatusCode.BadRequest) };
        yield return new object[] { GoogleError("invalidTransition", HttpStatusCode.BadRequest) };
        yield return new object[] { GoogleError("other", HttpStatusCode.Unauthorized) };
        yield return new object[] { GoogleError("other", HttpStatusCode.NotFound) };
        yield return new object[] { GoogleError("other", HttpStatusCode.TooManyRequests) };
        yield return new object[] { GoogleError("other", HttpStatusCode.InternalServerError) };
    }

    private static GoogleApiException GoogleError(string reason, HttpStatusCode status) =>
        new("youtube", reason)
        {
            Error = new RequestError { Errors = new List<SingleError> { new() { Reason = reason } } },
            HttpStatusCode = status,
        };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public void Every_error_explanation_is_bilingual(Exception exception)
    {
        var message = FriendlyError.Describe(exception);

        AssertBothLanguages(message, exception.GetType().Name);
        AssertEnglishHasNoChinese(message, exception.GetType().Name);
    }

    // ---------------------------------------------------------------- telegram

    [Theory]
    [InlineData("{\"description\":\"chat not found\"}")]
    [InlineData("{\"description\":\"bot was kicked\"}")]
    [InlineData("{\"description\":\"unauthorized\"}")]
    [InlineData("{\"description\":\"not enough rights\"}")]
    [InlineData("not json at all")]
    public void Every_telegram_failure_explanation_is_bilingual(string body)
    {
        var explain = typeof(LiveControlPanel.Notify.TelegramClient)
            .GetMethod("Explain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var message = (Msg)explain.Invoke(null, new object[] { body })!;

        AssertBothLanguages(message, "telegram: " + body);
        AssertEnglishHasNoChinese(message, "telegram: " + body);
    }

    [Fact]
    public async Task Telegram_configuration_errors_are_bilingual()
    {
        using var host = new TestHost();
        host.SetToday();

        // No broadcast yet.
        AssertBothLanguages((await host.Notifications.SendCurrentAsync()).Message, "no link");

        await host.Orchestrator.StartTodayAsync();
        await host.Notifications.SendCurrentAsync();

        // Second send is the idempotent path.
        AssertBothLanguages((await host.Notifications.SendCurrentAsync()).Message, "already sent");
    }

    // ---------------------------------------------------------------- slides

    [Fact]
    public void Slide_control_messages_are_bilingual()
    {
        var root = Path.Combine(Path.GetTempPath(), "lcp-i18n", Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigStore(new AppPaths(root));
            var controller = new LiveControlPanel.Slides.SlideController(
                config, Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveControlPanel.Slides.SlideController>.Instance);

            // Disabled.
            AssertBothLanguages(controller.Next().Message, "slides disabled next");
            AssertBothLanguages(controller.Goto(3).Message, "slides disabled goto");

            // Enabled but nothing presenting.
            config.UpdateSettings(s => s.Slides.Enabled = true);
            AssertBothLanguages(controller.Next().Message, "slides no show");
            AssertBothLanguages(controller.Goto(0).Message, "slides bad page");
            AssertBothLanguages(controller.Goto(3).Message, "slides cannot jump");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { /* temp dir */ }
        }
    }

    // ---------------------------------------------------------------- wire format

    /// <summary>
    /// The page resolves messages itself, so the shape on the wire is part of the contract:
    /// both languages present under camelCase names.
    /// </summary>
    [Fact]
    public void A_message_serializes_with_both_languages()
    {
        var json = JsonSerializer.Serialize(new Msg("中文", "English"), Json.Options);

        Assert.Contains("\"zh\"", json);
        Assert.Contains("\"en\"", json);

        var restored = JsonSerializer.Deserialize<Msg>(json, Json.Options)!;
        Assert.Equal("中文", restored.Zh);
        Assert.Equal("English", restored.En);
    }

    [Fact]
    public void A_state_snapshot_carries_both_languages_for_every_message_it_contains()
    {
        using var host = new TestHost();
        host.SetToday();

        var items = host.Preflight.RunAsync().GetAwaiter().GetResult();
        host.State.Mutate(s => s.Preflight = items);

        var json = JsonSerializer.Serialize(host.State.Snapshot(), Json.Options);
        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.GetProperty("preflight").EnumerateArray())
        {
            var message = item.GetProperty("message");
            Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("zh").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("en").GetString()));
        }
    }
}
