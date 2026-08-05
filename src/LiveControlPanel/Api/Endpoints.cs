using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Net;
using LiveControlPanel.Slides;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Api;

public sealed record ConfirmRequest([property: JsonPropertyName("confirm")] bool Confirm);

public sealed record SceneRequest(string Scene);

public sealed record GotoRequest(int Slide);

public sealed record CreateBroadcastBody(
    string? TemplateId,
    DateTime? Date,
    string? Title,
    string? Description);

public sealed record ApiResult(bool Ok, string Message, int? FailedStep = null);

[SupportedOSPlatform("windows")]
public static class Endpoints
{
    public static void MapPanelEndpoints(this WebApplication app)
    {
        MapState(app);
        MapBroadcast(app);
        MapObsAndSlides(app);
        MapTelegram(app);
        MapAccess(app);
        MapAuth(app);
        MapSettings(app);
        MapDiagnostics(app);
    }

    // ---------------------------------------------------------------- state

    private static void MapState(WebApplication app)
    {
        app.MapGet("/api/state", (StateManager state) => Results.Json(state.Snapshot(), Json.Options));

        app.MapGet("/api/preflight", async (Preflight preflight, StateManager state, CancellationToken ct) =>
        {
            var items = await preflight.RunAsync(ct);
            state.Mutate(s => s.Preflight = items);
            return Results.Json(state.Snapshot(), Json.Options);
        });
    }

    // ---------------------------------------------------------------- broadcast

    private static void MapBroadcast(WebApplication app)
    {
        app.MapPost("/api/broadcast/start-today", async (Orchestrator orchestrator, CancellationToken ct) =>
        {
            var outcome = await orchestrator.StartTodayAsync(ct: ct);
            return Outcome(outcome);
        });

        app.MapPost("/api/broadcast/retry/{step:int}",
            async (int step, Orchestrator orchestrator, CancellationToken ct) =>
            {
                if (step is < Orchestrator.StepCreate or > Orchestrator.StepAwaitLive)
                    return Results.BadRequest(new ApiResult(false, "无效的步骤编号。"));

                var outcome = await orchestrator.StartTodayAsync(step, ct);
                return Outcome(outcome);
            });

        // FR 6.1 "不是这一场？" override, and the ad-hoc "custom" template path.
        app.MapPost("/api/broadcast/create", (
            CreateBroadcastBody body, ConfigStore config, StateManager state) =>
        {
            if (state.Read(s => s.Broadcast) is { Id: not null })
                return Results.BadRequest(new ApiResult(false, "已经有一场直播了。请先结束它，再开始另一场。"));

            var date = body.Date ?? DateTime.Now;
            var template = body.TemplateId is null ? null : config.FindTemplate(body.TemplateId);

            if (body.TemplateId is not null && template is null)
                return Results.BadRequest(new ApiResult(false, "找不到这个场次。"));

            var title = !string.IsNullOrWhiteSpace(body.Title)
                ? body.Title!
                : template is null ? "" : ScheduleMatcher.FormatTitle(template, date);

            if (string.IsNullOrWhiteSpace(title))
                return Results.BadRequest(new ApiResult(false, "请填写直播标题。"));

            var scheduledStart = date;
            if (template is not null && ScheduleMatcher.TryParseStartTime(template.StartTime, out var startTime))
                scheduledStart = date.Date.Add(startTime);

            state.Mutate(s =>
            {
                s.Today = new TodayState
                {
                    TemplateId = template?.Id,
                    Title = title,
                    ScheduledStart = scheduledStart,
                    // Per-run override, kept out of the stored template.
                    Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description,
                    Manual = true,
                };
                s.Steps = new List<StepState>();
                s.Telegram = new TelegramState();
            });

            return Results.Json(new ApiResult(true, $"已选择「{title}」。"), Json.Options);
        });

        // FR 4.3: the panel requires an explicit confirm flag; the UI puts a real dialog in front of it.
        app.MapPost("/api/broadcast/stop", async (
            ConfirmRequest body, Orchestrator orchestrator, CancellationToken ct) =>
        {
            if (!body.Confirm)
                return Results.BadRequest(new ApiResult(false, "需要确认后才能结束直播。"));

            var outcome = await orchestrator.StopAsync(ct);
            return Outcome(outcome);
        });

        app.MapPost("/api/broadcast/end-previous", async (Orchestrator orchestrator, CancellationToken ct) =>
            Outcome(await orchestrator.EndPreviousAsync(ct)));

        app.MapPost("/api/broadcast/start-another", (Orchestrator orchestrator) =>
            orchestrator.StartAnother()
                ? Results.Json(new ApiResult(true, "可以开始下一场了。"), Json.Options)
                : Results.BadRequest(new ApiResult(false, "当前这场还没有结束。")));
    }

    // ---------------------------------------------------------------- obs / slides

    private static void MapObsAndSlides(WebApplication app)
    {
        app.MapPost("/api/obs/scene", async (
            SceneRequest body, Obs.IObsClient obs, StateManager state, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Scene))
                return Results.BadRequest(new ApiResult(false, "请指定画面名称。"));

            try
            {
                await obs.SetSceneAsync(body.Scene, ct);
                state.RecordAction($"切换画面到「{body.Scene}」");
                return Results.Json(new ApiResult(true, $"已切到「{body.Scene}」。"), Json.Options);
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResult(false, FriendlyError.Describe(ex)), Json.Options);
            }
        });

        app.MapPost("/api/slides/next", (ISlideController slides, StateManager state) =>
            SlideResponse(slides.Next(), state));

        app.MapPost("/api/slides/prev", (ISlideController slides, StateManager state) =>
            SlideResponse(slides.Previous(), state));

        app.MapPost("/api/slides/goto", (GotoRequest body, ISlideController slides, StateManager state) =>
            SlideResponse(slides.Goto(body.Slide), state));

        // Next-slide preview. 404 when the presentation program exposes no way to render a slide —
        // the operator page hides the preview block on failure rather than showing a broken image.
        app.MapGet("/api/slides/preview", (ISlideController slides, int? n) =>
        {
            var preview = slides.TryGetPreview(n);
            if (preview is null) return Results.NotFound();

            return Results.File(preview.Png, "image/png",
                lastModified: null, entityTag: null, enableRangeProcessing: false);
        });
    }

    private static IResult SlideResponse(SlideResult result, StateManager state)
    {
        state.RefreshSlides();
        return Results.Json(new ApiResult(result.Ok, result.Message), Json.Options);
    }

    // ---------------------------------------------------------------- telegram

    private static void MapTelegram(WebApplication app)
    {
        app.MapPost("/api/telegram/send", async (NotificationService notifications, CancellationToken ct) =>
        {
            var result = await notifications.SendCurrentAsync(ct);
            return Results.Json(new ApiResult(result.Ok, result.Message), Json.Options);
        });

        app.MapPost("/api/telegram/test", async (
            NotificationService notifications, AccessGate gate, HttpContext context, CancellationToken ct) =>
        {
            if (!gate.IsValidPin(context)) return PinRequired();

            var result = await notifications.SendTestAsync(ct);
            return Results.Json(new ApiResult(result.Ok, result.Message), Json.Options);
        });
    }

    // ---------------------------------------------------------------- access info

    private static void MapAccess(WebApplication app)
    {
        app.MapGet("/api/access-info", (AccessInfoProvider provider) =>
            Results.Json(provider.Get(), Json.Options));
    }

    // ---------------------------------------------------------------- oauth

    private static void MapAuth(WebApplication app)
    {
        app.MapGet("/auth/start", (YouTubeAuth auth, ConfigStore config) =>
        {
            if (!auth.IsConfigured)
                return Results.BadRequest(new ApiResult(false,
                    "请先在设置页填写 YouTube 的 Client ID 与 Client Secret。"));

            return Results.Redirect(auth.BuildAuthorizationUrl(config.Settings.Port));
        });

        // No access code here: the redirect comes from Google and cannot carry ours.
        app.MapGet("/auth/callback", async (
            HttpContext context, YouTubeAuth auth, ConfigStore config, CancellationToken ct) =>
        {
            var error = context.Request.Query["error"].FirstOrDefault();
            if (!string.IsNullOrEmpty(error))
                return Html($"<h1>授权未完成</h1><p>Google 返回：{error}</p><p>请回到设置页重试。</p>");

            var code = context.Request.Query["code"].FirstOrDefault();
            if (string.IsNullOrEmpty(code))
                return Html("<h1>授权未完成</h1><p>没有收到授权码，请回到设置页重试。</p>");

            try
            {
                await auth.ExchangeCodeAsync(code, config.Settings.Port, ct);
                return Html("<h1>授权成功</h1><p>可以关闭这个页面，回到控制面板了。</p>");
            }
            catch (Exception ex)
            {
                return Html($"<h1>授权失败</h1><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>");
            }
        });

        app.MapPost("/api/auth/revoke", async (
            YouTubeAuth auth, AccessGate gate, HttpContext context, CancellationToken ct) =>
        {
            if (!gate.IsValidPin(context)) return PinRequired();

            await auth.RevokeAsync(ct);
            return Results.Json(new ApiResult(true, "已清除授权，请重新授权。"), Json.Options);
        });
    }

    // ---------------------------------------------------------------- settings (PIN protected)

    private static void MapSettings(WebApplication app)
    {
        app.MapGet("/api/settings", (ConfigStore config, AccessGate gate, HttpContext context) =>
            gate.IsValidPin(context)
                ? Results.Json(config.Settings, Json.Options)
                : PinRequired());

        app.MapPut("/api/settings", (
            AppSettings body, ConfigStore config, AccessGate gate, HttpContext context, StateManager state) =>
        {
            if (!gate.IsValidPin(context)) return PinRequired();

            config.UpdateSettings(current =>
            {
                // Port is deliberately not editable here: changing it would strand every iPad's
                // home-screen icon and needs a firewall change anyway.
                current.StreamId = body.StreamId ?? current.StreamId;
                current.DefaultDescription = body.DefaultDescription ?? current.DefaultDescription;
                current.DefaultThumbnail = body.DefaultThumbnail ?? current.DefaultThumbnail;
                current.TelegramBotToken = body.TelegramBotToken ?? current.TelegramBotToken;
                current.TelegramChatId = body.TelegramChatId ?? current.TelegramChatId;
                current.TelegramMessageDefault = body.TelegramMessageDefault ?? current.TelegramMessageDefault;
                if (body.Obs is not null) current.Obs = body.Obs;
                if (body.Slides is not null) current.Slides = body.Slides;
                if (body.MatchWindow is not null) current.MatchWindow = body.MatchWindow;
                if (body.YouTube is not null)
                {
                    current.YouTube.ClientId = body.YouTube.ClientId ?? current.YouTube.ClientId;
                    current.YouTube.ClientSecret = body.YouTube.ClientSecret ?? current.YouTube.ClientSecret;
                    if (body.YouTube.AssumedValidityDays > 0)
                        current.YouTube.AssumedValidityDays = body.YouTube.AssumedValidityDays;
                }
                if (!string.IsNullOrWhiteSpace(body.SettingsPin)) current.SettingsPin = body.SettingsPin;
            });

            // Push the effect immediately instead of waiting for the background poll. Ticking
            // "enable slide control" and watching the operator page not change for five seconds
            // reads as "the setting did not save".
            state.RefreshSlides();

            return Results.Json(new ApiResult(true, "设置已保存。"), Json.Options);
        });

        app.MapGet("/api/templates", (ConfigStore config, AccessGate gate, HttpContext context) =>
            gate.IsValidPin(context)
                ? Results.Json(config.Templates, Json.Options)
                : PinRequired());

        // Unprotected read for the operator page's "不是这一场？" list: names and times only.
        //
        // defaultTitle is rendered here rather than in the browser so the date comes from this PC's
        // clock. An iPad with the wrong date or time zone must not be able to mint a wrong title —
        // and a title cannot be changed once the broadcast is created.
        app.MapGet("/api/templates/list", (ConfigStore config) => Results.Json(
            config.Templates.Select(t => new
            {
                t.Id,
                t.Name,
                t.StartTime,
                t.Weekdays,
                DefaultTitle = ScheduleMatcher.FormatTitle(t, DateTime.Now),
            }), Json.Options));

        app.MapPut("/api/templates", (
            List<ServiceTemplate> body, ConfigStore config, AccessGate gate, HttpContext context) =>
        {
            if (!gate.IsValidPin(context)) return PinRequired();

            if (body.Count == 0)
                return Results.BadRequest(new ApiResult(false, "至少要保留一个场次。"));

            if (body.Any(t => string.IsNullOrWhiteSpace(t.Id)))
                return Results.BadRequest(new ApiResult(false, "每个场次都需要一个 id。"));

            if (body.Select(t => t.Id.ToLowerInvariant()).Distinct().Count() != body.Count)
                return Results.BadRequest(new ApiResult(false, "场次 id 不能重复。"));

            config.SaveTemplates(body);
            return Results.Json(new ApiResult(true, "场次已保存。"), Json.Options);
        });

        app.MapPost("/api/stream-key/create", async (
            IYouTubeClient youtube, ConfigStore config, AccessGate gate, HttpContext context,
            CancellationToken ct) =>
        {
            if (!gate.IsValidPin(context)) return PinRequired();

            try
            {
                var key = await youtube.CreateReusableStreamAsync("Live Control Panel (reusable)", ct);
                config.UpdateSettings(s => s.StreamId = key.StreamId);
                return Results.Json(new
                {
                    ok = true,
                    message = "已创建推流密钥。请把下面的密钥填进 OBS 的「设置 → 推流 → 串流密钥」，此后不必再改。",
                    streamId = key.StreamId,
                    ingestionKey = key.IngestionKey,
                    ingestionAddress = key.IngestionAddress,
                }, Json.Options);
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResult(false, FriendlyError.Describe(ex)), Json.Options);
            }
        });
    }

    // ---------------------------------------------------------------- diagnostics

    private static void MapDiagnostics(WebApplication app)
    {
        // FR 5.3: the WPS slide-show window class is unknown and version-dependent, so it is
        // discovered here at deploy time rather than hard-coded.
        app.MapGet("/api/diag/windows", (ISlideController slides, AccessGate gate, HttpContext context) =>
            gate.IsValidPin(context)
                ? Results.Json(slides.EnumerateWindows(), Json.Options)
                : PinRequired());

        // FR 5.3's optional COM layer only claims PowerPoint compatibility in WPS, and window
        // handles plus the COM running-object table are per-session. Both questions are answered
        // here, on the machine that matters, instead of being assumed.
        app.MapGet("/api/diag/slides", (ISlideController slides, AccessGate gate, HttpContext context) =>
            gate.IsValidPin(context)
                ? Results.Json(slides.Diagnose(), Json.Options)
                : PinRequired());

        // Walks the automation chain member by member and names the step that fails. This is how the
        // WPS question gets answered on the church PC without guessing from documentation.
        app.MapGet("/api/diag/com-probe", (ISlideController slides, AccessGate gate, HttpContext context) =>
            gate.IsValidPin(context)
                ? Results.Json(new { ok = true, report = slides.ProbeCom() }, Json.Options)
                : PinRequired());

        app.MapGet("/api/diag/obs-inputs", async (
            Obs.IObsClient obs, AccessGate gate, HttpContext context, CancellationToken ct) =>
        {
            if (!gate.IsValidPin(context)) return PinRequired();

            try { return Results.Json(await obs.GetInputNamesAsync(ct), Json.Options); }
            catch (Exception ex) { return Results.Json(new ApiResult(false, FriendlyError.Describe(ex)), Json.Options); }
        });
    }

    // ---------------------------------------------------------------- helpers

    private static IResult Outcome(StartOutcome outcome) =>
        Results.Json(new ApiResult(outcome.Ok, outcome.Message, outcome.FailedStep), Json.Options);

    private static IResult PinRequired() =>
        Results.Json(new ApiResult(false, "需要设置密码（PIN）。"), Json.Options, statusCode: 403);

    private static IResult Html(string body) => Results.Content("""
        <!doctype html>
        <html lang="zh"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>YouTube 授权</title>
        <style>body{font-family:system-ui,sans-serif;margin:3rem auto;max-width:32rem;padding:0 1rem;line-height:1.6}</style>
        </head><body>{{BODY}}</body></html>
        """.Replace("{{BODY}}", body), "text/html; charset=utf-8");
}
