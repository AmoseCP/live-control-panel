using System.Net;
using System.Net.Http.Json;
using LiveControlPanel.Api;
using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Net;
using LiveControlPanel.Notify;
using LiveControlPanel.Obs;
using LiveControlPanel.Slides;
using LiveControlPanel.Youtube;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Hosts the real endpoint table over a TestServer.
///
/// This exists because a purely unit-level suite cannot see route-construction failures. ASP.NET Core
/// inspects handler parameter types when it materializes endpoints, and one accidentally
/// convention-named interface method (a <c>BindAsync</c>) once broke *every* route in the app while
/// every unit test still passed. Materializing the route table is the check that catches that class
/// of bug.
/// </summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private TestHost _fixtures = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _code = "";
    private string _pin = "";

    public async Task InitializeAsync()
    {
        _fixtures = new TestHost();
        _code = _fixtures.Config.Settings.AccessCode;
        _pin = _fixtures.Config.Settings.SettingsPin;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(_fixtures.Paths);
        builder.Services.AddSingleton(_fixtures.Config);
        builder.Services.AddSingleton<AccessGate>();
        builder.Services.AddSingleton(_fixtures.Hub);
        builder.Services.AddSingleton<AccessInfoProvider>();
        builder.Services.AddSingleton<ISlideController>(_fixtures.Slides);
        builder.Services.AddSingleton(_fixtures.State);
        builder.Services.AddSingleton<YouTubeAuth>();
        builder.Services.AddSingleton<IYouTubeClient>(_fixtures.YouTube);
        builder.Services.AddSingleton<IObsClient>(_fixtures.Obs);
        builder.Services.AddSingleton<ITelegramClient>(_fixtures.Telegram);
        builder.Services.AddSingleton(_fixtures.Preflight);
        builder.Services.AddSingleton(_fixtures.Orchestrator);
        builder.Services.AddSingleton(_fixtures.Notifications);

        _app = builder.Build();
        _app.UseWebSockets();
        _app.UseAccessGate();
        _app.MapPanelEndpoints();

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _fixtures.Dispose();
    }

    // ---------------------------------------------------------------- route table

    [Fact]
    public void The_whole_route_table_materializes()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.NotEmpty(endpoints);

        var routes = endpoints.OfType<RouteEndpoint>().Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .ToHashSet();

        // Every path in the requirements' API contract (FR 7).
        foreach (var expected in new[]
                 {
                     "/api/state", "/api/preflight",
                     "/api/broadcast/start-today", "/api/broadcast/retry/{step:int}",
                     "/api/broadcast/create", "/api/broadcast/stop", "/api/broadcast/end-previous",
                     "/api/obs/scene",
                     "/api/slides/next", "/api/slides/prev", "/api/slides/goto",
                     "/api/telegram/send",
                     "/api/access-info",
                     "/auth/start", "/auth/callback",
                     "/api/settings", "/api/templates",
                     "/api/stream-key/create",
                     "/api/diag/windows",
                 })
        {
            Assert.Contains(expected, routes);
        }
    }

    // ---------------------------------------------------------------- access gate

    [Theory]
    [InlineData("/api/state")]
    [InlineData("/api/access-info")]
    [InlineData("/api/preflight")]
    public async Task Api_requests_without_an_access_code_are_refused(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_access_code_is_refused()
    {
        var response = await _client.GetAsync("/api/state?k=definitely-wrong");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_access_code_is_accepted_in_the_query_string_or_a_header()
    {
        Assert.True((await _client.GetAsync($"/api/state?k={_code}")).IsSuccessStatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Add(AccessGate.CodeHeader, _code);
        Assert.True((await _client.SendAsync(request)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_oauth_callback_stays_reachable_without_an_access_code()
    {
        // Google's redirect cannot carry our code, so this one path must not be gated.
        var response = await _client.GetAsync("/auth/callback?error=access_denied");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("授权未完成", await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- state

    [Fact]
    public async Task State_returns_the_full_snapshot_shape()
    {
        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);

        Assert.NotNull(state);
        Assert.Contains(state!.Phase, new[] { Phase.NoSchedule, Phase.Ready, Phase.Live, Phase.Ended });
        Assert.NotNull(state.Obs);
        Assert.NotNull(state.Slides);
        Assert.NotNull(state.Telegram);
        Assert.NotNull(state.Auth);
    }

    [Fact]
    public async Task Preflight_runs_and_returns_all_five_checks()
    {
        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/preflight?k={_code}", Json.Options);

        Assert.Equal(5, state!.Preflight.Count);
        Assert.All(state.Preflight, item => Assert.False(string.IsNullOrWhiteSpace(item.Message.Zh)));
    }

    // ---------------------------------------------------------------- broadcast

    [Fact]
    public async Task Stop_without_a_confirm_flag_is_rejected()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/broadcast/stop?k={_code}", new { confirm = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResult>();
        Assert.Contains("确认", body!.Message.Zh);
    }

    [Fact]
    public async Task An_out_of_range_retry_step_is_rejected()
    {
        foreach (var step in new[] { 0, 7, 99 })
        {
            var response = await _client.PostAsync($"/api/broadcast/retry/{step}?k={_code}", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Manual_creation_picks_a_template_and_generates_its_title()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/broadcast/create?k={_code}",
            new { templateId = "sunday-service", date = new DateTime(2026, 8, 9) });

        Assert.True(response.IsSuccessStatusCode);

        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);
        Assert.Equal("8/9/2026 Sunday Service", state!.Today!.Title);
        Assert.Equal(new DateTime(2026, 8, 9, 10, 30, 0), state.Today.ScheduledStart);
    }

    [Fact]
    public async Task A_manual_choice_survives_a_later_state_read()
    {
        // The regression that mattered: schedule recomputation used to wipe an explicit choice.
        await _client.PostAsJsonAsync($"/api/broadcast/create?k={_code}",
            new { templateId = "custom", title = "8/5/2026 特别聚会" });

        for (var i = 0; i < 3; i++)
        {
            var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);
            Assert.Equal("8/5/2026 特别聚会", state!.Today!.Title);
        }
    }

    [Fact]
    public async Task Manual_creation_rejects_an_unknown_template()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/broadcast/create?k={_code}", new { templateId = "no-such-thing" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// An ad-hoc stream with no title supplied defaults to "<date> Service" from this machine's
    /// clock, so the operator never hand-types a date.
    /// </summary>
    [Fact]
    public async Task An_ad_hoc_stream_defaults_to_todays_date_plus_service()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/broadcast/create?k={_code}", new { templateId = "custom" });

        Assert.True(response.IsSuccessStatusCode);

        var today = DateTime.Now;
        var expected = $"{today.Month}/{today.Day}/{today.Year} Service";

        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);
        Assert.Equal(expected, state!.Today!.Title);
    }

    /// <summary>
    /// An ad-hoc stream is always "today, right now" — the date is the current date and the
    /// scheduled start is the current moment, not midnight and not a template time.
    /// </summary>
    [Fact]
    public async Task An_ad_hoc_stream_is_scheduled_for_this_moment_not_midnight()
    {
        var before = DateTime.Now;
        await _client.PostAsJsonAsync($"/api/broadcast/create?k={_code}", new { templateId = "custom" });
        var after = DateTime.Now;

        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);
        var scheduled = state!.Today!.ScheduledStart;

        Assert.NotNull(scheduled);
        Assert.InRange(scheduled!.Value, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.NotEqual(before.Date, scheduled.Value);   // not snapped to midnight
        Assert.Equal(before.Date, scheduled.Value.Date); // but still today
    }

    [Fact]
    public async Task An_ad_hoc_title_can_be_overridden()
    {
        await _client.PostAsJsonAsync($"/api/broadcast/create?k={_code}",
            new { templateId = "custom", title = "8/5/2026 特别聚会" });

        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);
        Assert.Equal("8/5/2026 特别聚会", state!.Today!.Title);
    }

    /// <summary>
    /// The default title is rendered server-side. A tablet with a wrong date or time zone must not be
    /// able to mint a title for the wrong day — a title cannot be corrected once the broadcast exists.
    /// </summary>
    [Fact]
    public async Task The_template_list_supplies_a_server_rendered_default_title()
    {
        var response = await _client.GetAsync($"/api/templates/list?k={_code}");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var custom = doc.RootElement.EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "custom");

        var today = DateTime.Now;
        Assert.Equal($"{today.Month}/{today.Day}/{today.Year} Service",
            custom.GetProperty("defaultTitle").GetString());

        // Unpadded, per FR 4.1 — the exact mistake hand-typing would introduce.
        Assert.DoesNotContain("/0", custom.GetProperty("defaultTitle").GetString()!);
    }

    // ---------------------------------------------------------------- slides

    [Fact]
    public async Task Slide_paging_reaches_the_controller()
    {
        await _client.PostAsync($"/api/slides/next?k={_code}", null);
        await _client.PostAsync($"/api/slides/prev?k={_code}", null);
        await _client.PostAsJsonAsync($"/api/slides/goto?k={_code}", new { slide = 12 });

        Assert.Equal(new[] { "next", "prev", "goto:12" }, _fixtures.Slides.Calls.ToArray());
    }

    // ---------------------------------------------------------------- slide preview

    [Fact]
    public async Task The_preview_returns_a_png_of_the_next_slide()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        _fixtures.Slides.Preview = new SlidePreview(png, 8, 24);

        var response = await _client.GetAsync($"/api/slides/preview?k={_code}");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// WPS may not support rendering a slide at all. A 404 is the signal the operator page uses to
    /// hide the preview block, so it must not be an error page or an empty 200.
    /// </summary>
    [Fact]
    public async Task The_preview_returns_404_when_the_program_cannot_render_a_slide()
    {
        _fixtures.Slides.Preview = null;

        var response = await _client.GetAsync($"/api/slides/preview?k={_code}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_preview_passes_through_an_explicit_slide_number()
    {
        _fixtures.Slides.Preview = new SlidePreview(new byte[] { 1 }, 12, 24);

        await _client.GetAsync($"/api/slides/preview?n=12&k={_code}");
        await _client.GetAsync($"/api/slides/preview?k={_code}");

        // Explicit number forwarded; omitting it asks the controller for "the next slide".
        Assert.Equal(new int?[] { 12, null }, _fixtures.Slides.PreviewRequests.ToArray());
    }

    [Fact]
    public async Task The_preview_is_behind_the_access_code()
    {
        _fixtures.Slides.Preview = new SlidePreview(new byte[] { 1 }, 8, 24);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync("/api/slides/preview")).StatusCode);
    }

    // ---------------------------------------------------------------- slide diagnostics

    [Fact]
    public async Task Slide_diagnostics_need_the_pin_and_report_every_capability()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync($"/api/diag/slides?k={_code}")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/diag/slides?k={_code}");
        request.Headers.Add(AccessGate.PinHeader, _pin);
        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);

        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var field in new[]
                 {
                     "sessionId", "sessionIsolated", "comProgId", "slideShowRunning",
                     "current", "total", "previewSupported", "targetWindowFound", "strategy",
                 })
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"missing field: {field}");
        }
    }

    // ---------------------------------------------------------------- access info

    [Fact]
    public async Task Access_info_carries_the_code_and_a_qr_code()
    {
        var info = await _client.GetFromJsonAsync<AccessInfo>($"/api/access-info?k={_code}", Json.Options);

        Assert.NotNull(info);
        Assert.Equal(_code, info!.AccessCode);
        Assert.Contains($"?k={_code}", info.LocalUrl);
        Assert.False(string.IsNullOrWhiteSpace(info.QrPngBase64));
    }

    // ---------------------------------------------------------------- pin protection

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/templates")]
    [InlineData("/api/diag/windows")]
    public async Task Settings_endpoints_need_the_pin(string path)
    {
        var withoutPin = await _client.GetAsync($"{path}?k={_code}");
        Assert.Equal(HttpStatusCode.Forbidden, withoutPin.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?k={_code}");
        request.Headers.Add(AccessGate.PinHeader, _pin);
        Assert.True((await _client.SendAsync(request)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_wrong_pin_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/settings?k={_code}");
        request.Headers.Add(AccessGate.PinHeader, "0000000");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// Rebinding the channel authorization is a settings-class action: with the access code alone,
    /// anyone who scanned the QR could hand the panel their own Google account. The PIN travels as
    /// ?pin= here because /auth/start is a top-level navigation and cannot carry a header.
    /// </summary>
    [Fact]
    public async Task Auth_start_needs_the_pin_and_accepts_it_as_a_query_parameter()
    {
        var withoutPin = await _client.GetAsync($"/auth/start?k={_code}");
        Assert.Equal(HttpStatusCode.Forbidden, withoutPin.StatusCode);

        // With the PIN it passes the gate; the YouTube client is unconfigured in tests, so the
        // endpoint's own validation answers 400 — anything but the gate's 403.
        var withPin = await _client.GetAsync($"/auth/start?k={_code}&pin={_pin}");
        Assert.Equal(HttpStatusCode.BadRequest, withPin.StatusCode);
    }

    /// <summary>
    /// Both gates answer 403, so the body has to say which one refused. Without this the settings page
    /// reported "wrong PIN" for a stale access code — sending the operator to fix the wrong thing, and
    /// discarding a PIN that was actually correct.
    /// </summary>
    [Fact]
    public async Task A_bad_access_code_and_a_bad_pin_are_distinguishable()
    {
        var badCode = await _client.GetAsync("/api/settings?k=definitely-wrong");
        Assert.Equal(HttpStatusCode.Forbidden, badCode.StatusCode);
        var codeBody = await badCode.Content.ReadFromJsonAsync<GateResult>();
        Assert.Equal(GateResult.BadAccessCode, codeBody!.Reason);
        Assert.Contains("访问码", codeBody.Message.Zh);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/settings?k={_code}");
        request.Headers.Add(AccessGate.PinHeader, "999999");
        var badPin = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, badPin.StatusCode);
        var pinBody = await badPin.Content.ReadFromJsonAsync<GateResult>();
        Assert.Equal(GateResult.PinRequired, pinBody!.Reason);
        Assert.Contains("密码", pinBody.Message.Zh);

        Assert.NotEqual(codeBody.Reason, pinBody.Reason);
    }

    [Fact]
    public async Task A_missing_pin_is_reported_as_a_pin_problem_not_an_access_problem()
    {
        var response = await _client.GetAsync($"/api/settings?k={_code}");

        var body = await response.Content.ReadFromJsonAsync<GateResult>();
        Assert.Equal(GateResult.PinRequired, body!.Reason);
    }

    [Fact]
    public async Task The_template_list_the_operator_page_uses_needs_no_pin()
    {
        var response = await _client.GetAsync($"/api/templates/list?k={_code}");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Sunday Service", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_settings_persists_and_never_changes_the_port()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/settings?k={_code}")
        {
            Content = JsonContent.Create(new
            {
                port = 9999,
                telegramChatId = "-1001234567890",
                obs = new { url = "ws://localhost:4455", sceneCamera = "Camera", audioInputName = "ProFX" },
            }),
        };
        request.Headers.Add(AccessGate.PinHeader, _pin);

        Assert.True((await _client.SendAsync(request)).IsSuccessStatusCode);

        Assert.Equal("-1001234567890", _fixtures.Config.Settings.TelegramChatId);
        Assert.Equal("Camera", _fixtures.Config.Settings.Obs.SceneCamera);
        // Changing the port would strand every iPad home-screen icon, so it is not editable here.
        Assert.NotEqual(9999, _fixtures.Config.Settings.Port);
    }

    /// <summary>
    /// Saving settings pushes the new slide state at once. Waiting for the five-second background
    /// poll made a saved setting look like it had not saved.
    /// </summary>
    [Fact]
    public async Task Saving_settings_refreshes_slide_state_immediately()
    {
        _fixtures.Slides.State = new SlidesState { Enabled = true, Available = true, Current = 3, Total = 9 };

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/settings?k={_code}")
        {
            Content = JsonContent.Create(new { telegramChatId = "-100999" }),
        };
        request.Headers.Add(AccessGate.PinHeader, _pin);
        Assert.True((await _client.SendAsync(request)).IsSuccessStatusCode);

        // No background service runs in this host, so a fresh value here can only have come from the
        // save path itself.
        var state = await _client.GetFromJsonAsync<RuntimeState>($"/api/state?k={_code}", Json.Options);
        Assert.True(state!.Slides.Enabled);
        Assert.Equal(3, state.Slides.Current);
        Assert.Equal(9, state.Slides.Total);
    }

    /// <summary>
    /// Regression for a data-loss bug. The PUT body used to bind to AppSettings, whose sections carry
    /// initializers and thus were never null — so a partial save (the Telegram test sends only three
    /// fields) silently replaced the OBS password, scene names, slide setup and match window with
    /// defaults. Against the old binding this test fails on every one of these assertions.
    /// </summary>
    [Fact]
    public async Task A_partial_settings_save_does_not_reset_untouched_sections()
    {
        _fixtures.Config.UpdateSettings(s =>
        {
            s.Obs.Password = "obs-secret";
            s.Obs.SceneCamera = "CamX";
            s.Slides.Enabled = true;
            s.Slides.WindowClass = "screenClass";
            s.MatchWindow.AfterMinutes = 90;
        });

        // The exact shape the settings page's "send a test message" button PUTs.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/settings?k={_code}")
        {
            Content = JsonContent.Create(new
            {
                telegramBotToken = "tok",
                telegramChatId = "-100123",
                telegramMessageDefault = "{title} {url}",
            }),
        };
        request.Headers.Add(AccessGate.PinHeader, _pin);
        Assert.True((await _client.SendAsync(request)).IsSuccessStatusCode);

        var settings = _fixtures.Config.Settings;
        Assert.Equal("tok", settings.TelegramBotToken);
        Assert.Equal("-100123", settings.TelegramChatId);

        // Everything the request did not mention is untouched.
        Assert.Equal("obs-secret", settings.Obs.Password);
        Assert.Equal("CamX", settings.Obs.SceneCamera);
        Assert.True(settings.Slides.Enabled);
        Assert.Equal("screenClass", settings.Slides.WindowClass);
        Assert.Equal(90, settings.MatchWindow.AfterMinutes);
    }

    /// <summary>
    /// An oversized window would let Wednesday's 04:40 window swallow the 18:00 service — the exact
    /// ambiguity the -60/+120 default was chosen to avoid.
    /// </summary>
    [Fact]
    public async Task Settings_reject_a_match_window_that_could_make_two_services_ambiguous()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/settings?k={_code}")
        {
            Content = JsonContent.Create(new { matchWindow = new { beforeMinutes = 60, afterMinutes = 100000 } }),
        };
        request.Headers.Add(AccessGate.PinHeader, _pin);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(120, _fixtures.Config.Settings.MatchWindow.AfterMinutes);   // unchanged
    }

    [Fact]
    public async Task Settings_reject_a_pin_the_numeric_unlock_keyboard_could_never_type()
    {
        foreach (var bad in new[] { "abcd", "123", "1234567", "12 34" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/settings?k={_code}")
            {
                Content = JsonContent.Create(new { settingsPin = bad }),
            };
            request.Headers.Add(AccessGate.PinHeader, _pin);

            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(_pin, _fixtures.Config.Settings.SettingsPin);
    }

    /// <summary>
    /// A weekday of 7 or a start time of "25:00" errors nowhere at save time — the service just never
    /// matches, discovered at 04:40 as an inexplicable "本日无排期".
    /// </summary>
    [Fact]
    public async Task Templates_reject_invalid_weekdays_and_invalid_start_times()
    {
        var badWeekday = await PutTemplates(new[]
        {
            new { id = "x", name = "X", weekdays = new[] { 7 }, startTime = "09:00" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badWeekday.StatusCode);

        var badTime = await PutTemplates(new[]
        {
            new { id = "x", name = "X", weekdays = new[] { 1 }, startTime = "25:99" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);

        Assert.Equal(5, _fixtures.Config.Templates.Count);   // seed untouched
    }

    [Fact]
    public async Task Templates_cannot_be_saved_empty_or_with_duplicate_ids()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PutTemplates(Array.Empty<object>())).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutTemplates(new[]
        {
            new { id = "dup", name = "A", weekdays = new[] { 1 }, startTime = "09:00" },
            new { id = "dup", name = "B", weekdays = new[] { 2 }, startTime = "09:00" },
        })).StatusCode);
    }

    private async Task<HttpResponseMessage> PutTemplates(object templates)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/templates?k={_code}")
        {
            Content = JsonContent.Create(templates),
        };
        request.Headers.Add(AccessGate.PinHeader, _pin);
        return await _client.SendAsync(request);
    }

    // ---------------------------------------------------------------- oauth

    [Fact]
    public async Task Auth_start_explains_itself_when_no_oauth_client_is_configured()
    {
        var response = await _client.GetAsync($"/auth/start?k={_code}&pin={_pin}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResult>();
        Assert.Contains("Client ID", body!.Message.Zh);
    }

    // ---------------------------------------------------------------- telegram

    [Fact]
    public async Task Telegram_send_reports_that_there_is_no_link_yet()
    {
        var response = await _client.PostAsync($"/api/telegram/send?k={_code}", null);

        var body = await response.Content.ReadFromJsonAsync<ApiResult>();
        Assert.False(body!.Ok);
        Assert.Contains("还没有创建直播", body.Message.Zh);
    }
}
