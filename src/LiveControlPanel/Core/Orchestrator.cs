using LiveControlPanel.Config;
using LiveControlPanel.Obs;
using LiveControlPanel.Translate;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Core;

public sealed record StartOutcome(bool Ok, int? FailedStep, Msg Message);

/// <summary>
/// The start-today sequence of FR 4.2, extended to the bilingual case.
///
/// Three properties are load-bearing:
/// <list type="bullet">
/// <item>Idempotent. Five taps on a slow morning must produce one broadcast and one stream, so every
/// step is guarded by the state it would have produced.</item>
/// <item>Resumable. A failure reports its step number and can be retried from there — never from
/// the beginning, which would create a second broadcast.</item>
/// <item>Observable. Progress is pushed after each step so the operator does not conclude it hung
/// and start tapping.</item>
/// </list>
///
/// The second, AI-translated broadcast rides along inside the same steps rather than getting its own
/// sequence, and it is subordinate throughout: it is created, bound and given a thumbnail alongside
/// the primary one, but nothing about it may fail the run. OBS is still asked to start exactly one
/// stream — obs-multi-rtmp's "sync start/stop with OBS" carries the second RTMP target off the same
/// video encoder — so there is no second output for this panel to get wrong.
/// </summary>
public sealed class Orchestrator
{
    public const int StepCreate = 1;
    public const int StepBind = 2;
    public const int StepThumbnail = 3;
    public const int StepTranslate = 4;
    public const int StepScene = 5;
    public const int StepStream = 6;
    public const int StepAwaitLive = 7;

    private static readonly (int Step, Msg Name)[] StepNames =
    {
        (StepCreate, new Msg("创建直播", "Create broadcast")),
        (StepBind, new Msg("绑定推流密钥", "Bind stream key")),
        (StepThumbnail, new Msg("上传封面", "Upload thumbnail")),
        (StepTranslate, new Msg("启动 AI 翻译", "Start AI translation")),
        (StepScene, new Msg("切换画面", "Switch scene")),
        (StepStream, new Msg("开始推流", "Start streaming")),
        (StepAwaitLive, new Msg("等待 YouTube 上线", "Wait for YouTube to go live")),
    };

    private static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LivePollTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the translated broadcast is given to come up after the primary one is live. Short on
    /// purpose: by then the service is already on air, and this is a warning, not a gate.
    ///
    /// Settable for the same reason <see cref="StateManager.Clock"/> is — the suite must be able to
    /// exercise the timeout without sitting out half a minute of real waiting.
    /// </summary>
    internal TimeSpan TranslatedLiveTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly ConfigStore _config;
    private readonly StateManager _state;
    private readonly IYouTubeClient _youtube;
    private readonly IObsClient _obs;
    private readonly ITranslationService _translation;
    private readonly ILogger<Orchestrator> _log;

    /// <summary>Serializes runs. Taken with a zero timeout so a concurrent tap is rejected, not queued.</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public Orchestrator(
        ConfigStore config,
        StateManager state,
        IYouTubeClient youtube,
        IObsClient obs,
        ITranslationService translation,
        ILogger<Orchestrator> log)
    {
        _config = config;
        _state = state;
        _youtube = youtube;
        _obs = obs;
        _translation = translation;
        _log = log;
    }

    /// <summary>Runs the sequence, or resumes it from <paramref name="fromStep"/>.</summary>
    public async Task<StartOutcome> StartTodayAsync(int fromStep = StepCreate, CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Not an error: the operator tapped twice. Report the run already in flight.
            return new StartOutcome(true, null, new Msg("正在开始直播，请稍候…", "Starting the broadcast, please wait…"));
        }

        try
        {
            var today = _state.Read(s => s.Today);
            if (today?.Title is null)
                return new StartOutcome(false, null, new Msg(
                    "今天没有排期。请先选择要开始的场次。",
                    "Nothing is scheduled. Pick the service you want to start first."));

            _state.Mutate(s =>
            {
                s.Starting = true;
                s.Steps = StepNames.Select(n => new StepState { Step = n.Step, Name = n.Name }).ToList();
            });

            try
            {
                return await RunStepsAsync(today, fromStep, ct).ConfigureAwait(false);
            }
            finally
            {
                _state.Mutate(s => s.Starting = false);
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<StartOutcome> RunStepsAsync(TodayState today, int fromStep, CancellationToken ct)
    {
        var template = today.TemplateId is null ? null : _config.FindTemplate(today.TemplateId);
        var plan = TranslationPlan.For(_config.Settings, template);

        for (var step = Math.Max(StepCreate, fromStep); step <= StepAwaitLive; step++)
        {
            SetStep(step, "running", null);

            try
            {
                var message = await RunStepAsync(step, today, template, plan, ct).ConfigureAwait(false);
                SetStep(step, StatusOf(message), message.Text);
            }
            catch (Exception ex)
            {
                var friendly = FriendlyError.Describe(ex);
                _log.LogError(ex, "start-today failed at step {Step}", step);
                SetStep(step, "failed", friendly);
                _state.RecordAction(new Msg($"开播失败（第 {step} 步）", $"Start failed at step {step}"), today.Title);
                return new StartOutcome(false, step, friendly);
            }
        }

        _state.RecordAction(new Msg("开始直播", "Started the broadcast"), today.Title);
        return new StartOutcome(true, null, new Msg("直播已开始。", "The broadcast has started."));
    }

    private static string StatusOf(StepMessage message) =>
        message.Warn ? "warn" : message.Skipped ? "skipped" : "done";

    /// <summary>
    /// A step's outcome. <see cref="Warn"/> is the bilingual case's whole safety story: the step did
    /// not do what it was asked, the sequence continues anyway, and no retry is offered — because
    /// everything that can warn is something the congregation's stream does not depend on.
    /// </summary>
    private sealed record StepMessage(Msg? Text, bool Skipped = false, bool Warn = false);

    private async Task<StepMessage> RunStepAsync(
        int step, TodayState today, ServiceTemplate? template, TranslationPlan plan,
        CancellationToken ct) => step switch
    {
        StepCreate => await CreateAsync(today, template, plan, ct).ConfigureAwait(false),
        StepBind => await BindAsync(plan, ct).ConfigureAwait(false),
        StepThumbnail => await ThumbnailAsync(template, ct).ConfigureAwait(false),
        StepTranslate => await TranslateAsync(plan, ct).ConfigureAwait(false),
        StepScene => await SceneAsync(ct).ConfigureAwait(false),
        StepStream => await StreamAsync(ct).ConfigureAwait(false),
        StepAwaitLive => await AwaitLiveAsync(ct).ConfigureAwait(false),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown orchestration step."),
    };

    // ---------------------------------------------------------------- step 1: create

    private async Task<StepMessage> CreateAsync(
        TodayState today, ServiceTemplate? template, TranslationPlan plan, CancellationToken ct)
    {
        var settings = _config.Settings;
        var description = Coalesce(today.Description,
            Coalesce(template?.Description, settings.DefaultDescription));

        var primary = _state.Read(s => s.Broadcast);
        Msg primaryMessage;

        if (primary?.Id is not null)
        {
            primaryMessage = new Msg($"已存在直播 {primary.Id}", $"Broadcast {primary.Id} already exists");
        }
        else
        {
            var info = await CreateOneAsync(today.Title!, description, template, ct).ConfigureAwait(false);
            _state.Mutate(s => s.Broadcast = NewBroadcastState(info, _state.Clock()));
            primaryMessage = info.Adopted
                ? new Msg($"沿用已创建的 {info.Id}", $"Reusing existing {info.Id}")
                : new Msg($"已创建 {info.Id}", $"Created {info.Id}");
        }

        var skipped = primary?.Id is not null;

        if (!plan.Active) return new StepMessage(primaryMessage, Skipped: skipped);

        if (!plan.Ready)
        {
            return new StepMessage(new Msg(
                $"{primaryMessage.Zh}；未创建翻译直播：还没有配好第二条推流密钥或 Gemini API Key。",
                $"{primaryMessage.En}; no translated broadcast: the second stream key or the Gemini API " +
                "key is not configured yet."), Warn: true);
        }

        var existingTranslated = _state.Read(s => s.Translated);
        if (existingTranslated?.Id is not null)
        {
            return new StepMessage(new Msg(
                $"{primaryMessage.Zh}；翻译直播 {existingTranslated.Id} 已存在",
                $"{primaryMessage.En}; translated broadcast {existingTranslated.Id} already exists"),
                Skipped: skipped);
        }

        // The translated broadcast is best-effort from here down. Failing to create it must leave the
        // primary one — already created above — untouched and the sequence running.
        try
        {
            var translatedTitle = plan.TitleFor(today.Title!);
            var translated = await CreateOneAsync(translatedTitle, description, template, ct).ConfigureAwait(false);
            _state.Mutate(s => s.Translated = NewBroadcastState(translated, _state.Clock()));

            return new StepMessage(new Msg(
                $"{primaryMessage.Zh}；翻译直播 {translated.Id}",
                $"{primaryMessage.En}; translated broadcast {translated.Id}"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Creating the translated broadcast failed");
            return new StepMessage(new Msg(
                $"{primaryMessage.Zh}；翻译直播创建失败，本场只有原声。",
                $"{primaryMessage.En}; the translated broadcast could not be created, so this service is " +
                "original audio only."), Warn: true);
        }
    }

    private sealed record CreatedBroadcast(string Id, string Title, string WatchUrl, bool Adopted);

    /// <summary>
    /// Creates one broadcast, adopting a lost attempt of the same title rather than duplicating it.
    ///
    /// The second anchor, on YouTube's side: if a previous attempt's insert succeeded but the
    /// response was lost (timeout mid-create), the local state is empty while the broadcast exists.
    /// Titles carry the date, so an unfinished broadcast with this exact title is that lost attempt.
    /// The translated broadcast's title suffix is what keeps the two apart here.
    /// </summary>
    private async Task<CreatedBroadcast> CreateOneAsync(
        string title, string description, ServiceTemplate? template, CancellationToken ct)
    {
        try
        {
            var leftover = (await _youtube.ListUnfinishedBroadcastsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(b => b.Title == title);

            if (leftover is not null)
            {
                _log.LogInformation("Adopted existing broadcast {Id} \"{Title}\" instead of creating a duplicate",
                    leftover.Id, leftover.Title);
                return new CreatedBroadcast(leftover.Id, leftover.Title, leftover.WatchUrl, Adopted: true);
            }
        }
        catch (Exception ex)
        {
            // The check is best-effort: if the listing itself fails, creating is still the right
            // move — worst case is the duplicate the pre-flight already knows how to clean up.
            _log.LogDebug(ex, "Checking for an existing broadcast before create failed");
        }

        var info = await _youtube.CreateBroadcastAsync(new CreateBroadcastRequest(
            Title: title,
            Description: description,
            // The moment the operator actually pressed start, not the template's announced time.
            // Operators run early or late, and with enableAutoStart the broadcast goes live within
            // seconds of this call — so reporting the nominal 18:00 at 18:25 would just put a time
            // that already passed on the watch page. The nominal time still drives matching, the
            // title, and the "预定开始" line in the UI.
            ScheduledStart: DateTime.Now,
            PrivacyStatus: Coalesce(template?.PrivacyStatus, "unlisted"),
            MadeForKids: template?.MadeForKids ?? false,
            LatencyPreference: Coalesce(template?.LatencyPreference, "ultraLow")), ct).ConfigureAwait(false);

        return new CreatedBroadcast(info.Id, info.Title, info.WatchUrl, Adopted: false);
    }

    private static BroadcastState NewBroadcastState(CreatedBroadcast info, DateTime now) => new()
    {
        Id = info.Id,
        WatchUrl = info.WatchUrl,
        Status = BroadcastStatus.Created,
        Title = info.Title,
        CreatedOn = now,
    };

    // ---------------------------------------------------------------- step 2: bind

    private async Task<StepMessage> BindAsync(TranslationPlan plan, CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        var skipped = broadcast.Status is not BroadcastStatus.Created;

        if (!skipped)
        {
            var streamId = _config.Settings.StreamId;
            if (string.IsNullOrWhiteSpace(streamId))
                throw new LocalizedInvalidOperationException(new Msg(
                    "还没有创建推流密钥。请让管理员在设置页点击「创建推流密钥」，并把密钥填进 OBS。",
                    "No stream key has been created yet. Ask the administrator to create one on the settings " +
                    "page and enter it in OBS."));

            await _youtube.BindStreamAsync(broadcast.Id!, streamId, ct).ConfigureAwait(false);
            _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Bound);
        }

        var primaryMessage = skipped
            ? new Msg("已绑定", "Already bound")
            : new Msg("已绑定推流密钥", "Stream key bound");

        var translated = _state.Read(s => s.Translated);
        if (translated?.Id is null || translated.Status is not BroadcastStatus.Created)
            return new StepMessage(primaryMessage, Skipped: skipped);

        // Its own key, because one liveStream can only back one live broadcast at a time.
        try
        {
            await _youtube.BindStreamAsync(translated.Id, _config.Settings.Translation.StreamId, ct)
                .ConfigureAwait(false);
            _state.Mutate(s =>
            {
                if (s.Translated is not null) s.Translated.Status = BroadcastStatus.Bound;
            });

            return new StepMessage(new Msg(
                $"{primaryMessage.Zh}（两条）", $"{primaryMessage.En} (both)"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Binding the translated broadcast failed");
            return new StepMessage(new Msg(
                $"{primaryMessage.Zh}；翻译直播绑定失败，本场只有原声。",
                $"{primaryMessage.En}; the translated broadcast could not be bound, so this service is " +
                "original audio only."), Warn: true);
        }
    }

    // ---------------------------------------------------------------- step 3: thumbnail

    private async Task<StepMessage> ThumbnailAsync(ServiceTemplate? template, CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        var translated = _state.Read(s => s.Translated);

        var pendingPrimary = !broadcast.ThumbnailUploaded;
        var pendingTranslated = translated?.Id is not null && !translated.ThumbnailUploaded;

        if (!pendingPrimary && !pendingTranslated)
            return new StepMessage(new Msg("封面已上传", "Thumbnail already uploaded"), Skipped: true);

        var relative = Coalesce(template?.ThumbnailFile, _config.Settings.DefaultThumbnail);
        if (string.IsNullOrWhiteSpace(relative))
            return new StepMessage(new Msg("未配置封面", "No thumbnail configured"), Skipped: true);

        var path = _config.Paths.Resolve(relative);
        if (!File.Exists(path))
        {
            // A missing thumbnail is cosmetic; refusing to go live over it would be absurd.
            _log.LogWarning("Thumbnail {Path} not found; skipping upload", path);
            return new StepMessage(new Msg("找不到封面文件，已跳过", "Thumbnail file not found; skipped"), Skipped: true);
        }

        var contentType = ContentType(path);

        if (pendingPrimary)
        {
            await using var stream = File.OpenRead(path);
            await _youtube.SetThumbnailAsync(broadcast.Id!, stream, contentType, ct).ConfigureAwait(false);
            _state.Mutate(s => s.Broadcast!.ThumbnailUploaded = true);
        }

        if (!pendingTranslated) return new StepMessage(new Msg("封面已上传", "Thumbnail uploaded"));

        try
        {
            // A fresh stream: the upload above consumed the first one to its end.
            await using var stream = File.OpenRead(path);
            await _youtube.SetThumbnailAsync(translated!.Id!, stream, contentType, ct).ConfigureAwait(false);
            _state.Mutate(s =>
            {
                if (s.Translated is not null) s.Translated.ThumbnailUploaded = true;
            });

            return new StepMessage(new Msg("封面已上传（两条）", "Thumbnail uploaded (both)"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Uploading the translated broadcast's thumbnail failed");
            return new StepMessage(new Msg(
                "原声那条封面已上传；翻译那条没传上，不影响直播。",
                "The primary thumbnail is uploaded; the translated one failed, which does not affect the " +
                "broadcast."), Warn: true);
        }
    }

    // ---------------------------------------------------------------- step 4: translation

    /// <summary>
    /// Brings the AI translator up before OBS starts sending, so the translated audio track has
    /// content from the first frame rather than a silent opening minute.
    ///
    /// It cannot fail the run. Every path that is not "it started" ends in a warning, and the service
    /// goes on air either way — with the translated stream silent until someone fixes it.
    /// </summary>
    private async Task<StepMessage> TranslateAsync(TranslationPlan plan, CancellationToken ct)
    {
        if (!plan.Active)
            return new StepMessage(new Msg("本场不做翻译", "No translation for this service"), Skipped: true);

        var translated = _state.Read(s => s.Translated);
        if (translated?.Id is null)
            return new StepMessage(new Msg(
                "没有翻译直播，已跳过。", "No translated broadcast; skipped."), Skipped: true);

        try
        {
            var problem = await _translation.StartAsync(plan.TargetLanguage, ct).ConfigureAwait(false);
            if (problem is null)
                return new StepMessage(new Msg(
                    $"翻译已启动（{plan.TargetLanguage}）", $"Translation started ({plan.TargetLanguage})"));

            return new StepMessage(problem, Warn: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Starting the translator failed");
            return new StepMessage(FriendlyError.Describe(ex), Warn: true);
        }
    }

    // ---------------------------------------------------------------- steps 5-7

    private async Task<StepMessage> SceneAsync(CancellationToken ct)
    {
        var scene = _config.Settings.Obs.SceneCamera;
        if (string.IsNullOrWhiteSpace(scene)) return new StepMessage(new Msg("未配置起始画面", "No starting scene configured"), Skipped: true);

        if (string.Equals(_obs.Status.CurrentScene, scene, StringComparison.Ordinal))
            return new StepMessage(new Msg($"已在「{scene}」", $"Already on \"{scene}\""), Skipped: true);

        await _obs.SetSceneAsync(scene, ct).ConfigureAwait(false);
        return new StepMessage(new Msg($"已切到「{scene}」", $"Switched to \"{scene}\""));
    }

    /// <summary>
    /// One stream, exactly as before the second broadcast existed. The plugin's "sync start with OBS"
    /// is what puts the translated RTMP target on air, which is deliberate: there is no second output
    /// here to start, to get out of step, or to leave running.
    /// </summary>
    private async Task<StepMessage> StreamAsync(CancellationToken ct)
    {
        // Idempotency: OBS is already sending, so do not restart the output.
        if (_obs.Status.Streaming) return new StepMessage(new Msg("已在推流", "Already streaming"), Skipped: true);

        await _obs.StartStreamAsync(ct).ConfigureAwait(false);
        return new StepMessage(new Msg("已开始推流", "Streaming started"));
    }

    /// <summary>
    /// Polls until YouTube reports the broadcast live. enableAutoStart makes the transition automatic
    /// once frames arrive; this only waits for it so the operator knows the link really works.
    ///
    /// The translated broadcast is then given a short, separate wait. It depends on the OBS plugin
    /// being configured, which this panel cannot see — so its absence is reported as a warning naming
    /// the thing to check, never as a failed step on a service that is already on air.
    /// </summary>
    private async Task<StepMessage> AwaitLiveAsync(CancellationToken ct)
    {
        var broadcast = RequireBroadcast();

        if (broadcast.Status != BroadcastStatus.Live)
        {
            var deadline = DateTime.UtcNow + LivePollTimeout;
            var live = false;

            while (DateTime.UtcNow < deadline)
            {
                var status = await _youtube.GetLifeCycleStatusAsync(broadcast.Id!, ct).ConfigureAwait(false);

                if (status == "live")
                {
                    _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Live);
                    live = true;
                    break;
                }

                // Ended while we were waiting — stopped from this panel in a race, or in YouTube Studio.
                // Polling a finished broadcast for the remaining minute would end in a "retry this step"
                // that can never succeed.
                if (status is "complete" or "revoked")
                {
                    _state.Mutate(s =>
                    {
                        if (s.Broadcast is not null) s.Broadcast.Status = BroadcastStatus.Complete;
                    });
                    throw new LocalizedInvalidOperationException(new Msg(
                        "这场直播已经结束，不会再上线。若要再播一场，请点「开始另一场」。",
                        "This broadcast has already ended and will not go live. Use \"start another " +
                        "service\" to run a new one."));
                }

                if (status == "testing")
                    _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Testing);

                await Task.Delay(LivePollInterval, ct).ConfigureAwait(false);
            }

            if (!live)
                throw new LocalizedTimeoutException(new Msg(
                    "YouTube 还没有确认收到画面。推流可能仍在建立，请稍等十几秒后点「重试这一步」；若持续如此，请检查网络。",
                    "YouTube has not confirmed it is receiving video yet. The stream may still be establishing — " +
                    "wait about fifteen seconds and retry this step; if it persists, check the network."));
        }

        return await AwaitTranslatedLiveAsync(ct).ConfigureAwait(false);
    }

    private async Task<StepMessage> AwaitTranslatedLiveAsync(CancellationToken ct)
    {
        var translated = _state.Read(s => s.Translated);
        if (translated?.Id is null) return new StepMessage(new Msg("YouTube 已上线", "YouTube is live"));

        if (translated.Status == BroadcastStatus.Live)
            return new StepMessage(new Msg("两条都已上线", "Both broadcasts are live"));

        var deadline = DateTime.UtcNow + TranslatedLiveTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            string? status;
            try
            {
                status = await _youtube.GetLifeCycleStatusAsync(translated.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Polling the translated broadcast's status failed");
                break;
            }

            if (status == "live")
            {
                _state.Mutate(s =>
                {
                    if (s.Translated is not null) s.Translated.Status = BroadcastStatus.Live;
                });
                return new StepMessage(new Msg("两条都已上线", "Both broadcasts are live"));
            }

            if (status is "complete" or "revoked") break;

            if (status == "testing")
                _state.Mutate(s =>
                {
                    if (s.Translated is not null) s.Translated.Status = BroadcastStatus.Testing;
                });

            await Task.Delay(LivePollInterval, ct).ConfigureAwait(false);
        }

        return new StepMessage(new Msg(
            "原声那条已上线；翻译那条还没有收到画面。请在 OBS 的「多路推流」面板确认第二个目标已勾选" +
            "「与 OBS 同步开始」，并且填的是第二个推流密钥。",
            "The primary broadcast is live; the translated one is not receiving video. In the OBS " +
            "multi-RTMP dock, check that the second target has \"sync start with OBS\" ticked and carries " +
            "the second stream key."), Warn: true);
    }

    // ---------------------------------------------------------------- stop / cleanup

    /// <summary>FR 4.3. The translator is stopped first, then OBS stops sending, then both broadcasts
    /// are transitioned to complete.</summary>
    public async Task<StartOutcome> StopAsync(CancellationToken ct = default)
    {
        var broadcast = _state.Read(s => s.Broadcast);
        var translated = _state.Read(s => s.Translated);

        try
        {
            if (_obs.Status.Streaming) await _obs.StopStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stopping the OBS stream failed");

            // The translator is deliberately still running here. OBS is still pushing both RTMP
            // targets, and the operator has just been sent to stop it by hand — however long that
            // takes, the translated stream keeps its audio instead of going silent on the way out.
            return new StartOutcome(false, null, new Msg(
                "无法让 OBS 停止推流。请直接在 OBS 里点「停止推流」，然后再回到本页结束直播。",
                "OBS would not stop streaming. Stop it directly in OBS, then come back here to end the broadcast."));
        }

        // After OBS, not before: the cable outliving the stream by a moment is harmless, whereas a
        // translated stream that is still on air with no audio is not.
        try { await _translation.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Stopping the translator failed"); }

        if (broadcast?.Id is not null)
        {
            try
            {
                await _youtube.TransitionToCompleteAsync(broadcast.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Transitioning broadcast {Id} to complete failed", broadcast.Id);

                // The stream has stopped, which is what the congregation sees. Mark it ended locally
                // and say plainly that YouTube may still show it as live.
                _state.Mutate(s =>
                {
                    if (s.Broadcast is not null) s.Broadcast.Status = BroadcastStatus.Complete;
                });
                await CompleteTranslatedAsync(translated, ct).ConfigureAwait(false);
                _state.RecordAction(new Msg("停止直播（YouTube 未确认）", "Stopped (YouTube unconfirmed)"), broadcast.Title);
                return new StartOutcome(false, null, new Msg(
                    "推流已停止，但 YouTube 那边没有确认结束。请稍后在 YouTube Studio 里确认这场已结束。",
                    "Streaming has stopped, but YouTube did not confirm the broadcast ended. Check in " +
                    "YouTube Studio later that it shows as finished."));
            }

            _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Complete);
        }

        var translatedEnded = await CompleteTranslatedAsync(translated, ct).ConfigureAwait(false);

        _state.RecordAction(new Msg("停止直播", "Stopped the broadcast"), broadcast?.Title);

        return translatedEnded
            ? new StartOutcome(true, null, new Msg("直播已结束。", "The broadcast has ended."))
            : new StartOutcome(true, null, new Msg(
                "直播已结束，但翻译那条 YouTube 没有确认结束。请稍后在 YouTube Studio 里确认。",
                "The broadcast has ended, but YouTube did not confirm the translated one finished. Check it " +
                "in YouTube Studio later."));
    }

    /// <summary>Ends the translated broadcast. Its failure never changes the primary outcome.</summary>
    private async Task<bool> CompleteTranslatedAsync(BroadcastState? translated, CancellationToken ct)
    {
        if (translated?.Id is null) return true;
        if (translated.Status == BroadcastStatus.Complete) return true;

        try
        {
            await _youtube.TransitionToCompleteAsync(translated.Id, ct).ConfigureAwait(false);
            _state.Mutate(s =>
            {
                if (s.Translated is not null) s.Translated.Status = BroadcastStatus.Complete;
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Transitioning the translated broadcast {Id} to complete failed", translated.Id);
            _state.Mutate(s =>
            {
                if (s.Translated is not null) s.Translated.Status = BroadcastStatus.Complete;
            });
            return false;
        }
    }

    /// <summary>FR 4.4 one-click fix for the leftover-broadcast case.</summary>
    public async Task<StartOutcome> EndPreviousAsync(CancellationToken ct = default)
    {
        try
        {
            var unfinished = await _youtube.ListUnfinishedBroadcastsAsync(ct).ConfigureAwait(false);

            // Both of this run's broadcasts are excluded, not just the primary one — otherwise the
            // cleanup would end the translated broadcast it had created moments earlier.
            var mine = _state.Read(s => new HashSet<string>(
                new[] { s.Broadcast?.Id, s.Translated?.Id }
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!),
                StringComparer.Ordinal));

            var ended = 0;
            foreach (var broadcast in unfinished)
            {
                if (mine.Contains(broadcast.Id)) continue;

                try
                {
                    // Only a broadcast that actually went on air can transition to complete. A leftover
                    // that never started (created/ready) gets invalidTransition from YouTube — but it
                    // still holds the shared stream key, so it is deleted instead.
                    if (broadcast.LifeCycleStatus is "live" or "testing" or "liveStarting")
                        await _youtube.TransitionToCompleteAsync(broadcast.Id, ct).ConfigureAwait(false);
                    else
                        await _youtube.DeleteBroadcastAsync(broadcast.Id, ct).ConfigureAwait(false);
                }
                catch (Google.GoogleApiException api) when (api.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone on YouTube's side (removed in Studio, or stale in the listing).
                    // That is the outcome this loop exists to produce, so keep cleaning instead of
                    // aborting the whole batch on it.
                    _log.LogInformation("Broadcast {Id} was already gone; continuing cleanup", broadcast.Id);
                    continue;
                }

                ended++;
            }

            _state.RecordAction(new Msg($"结束遗留直播 {ended} 场", $"Ended {ended} leftover broadcast(s)"));
            return ended == 0
                ? new StartOutcome(true, null, new Msg("没有需要结束的直播。", "There was nothing to end."))
                : new StartOutcome(true, null, new Msg(
                    $"已结束 {ended} 场未完成的直播，现在可以开始今天的直播了。",
                    $"Ended {ended} unfinished broadcast(s). You can start today's broadcast now."));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ending previous broadcasts failed");
            return new StartOutcome(false, null, FriendlyError.Describe(ex));
        }
    }

    /// <summary>
    /// Clears the current broadcast so a second service can be started the same day (FR 6.1, Ended
    /// phase). Only allowed once the current one is finished.
    /// </summary>
    public bool StartAnother()
    {
        var status = _state.Read(s => s.Broadcast?.Status);
        if (status is not null && status != BroadcastStatus.Complete) return false;

        _state.Mutate(s =>
        {
            s.Broadcast = null;
            s.Translated = null;
            s.Today = null;
            s.Steps = new List<StepState>();
            s.Telegram = new TelegramState();
            s.Translation = new TranslationState();
        });
        return true;
    }

    /// <summary>
    /// Operator-facing recovery for a translated stream that went silent mid-service — the one thing
    /// they can usefully do about it without leaving the panel.
    /// </summary>
    public async Task<StartOutcome> RestartTranslationAsync(CancellationToken ct = default)
    {
        var today = _state.Read(s => s.Today);
        var template = today?.TemplateId is null ? null : _config.FindTemplate(today.TemplateId);
        var plan = TranslationPlan.For(_config.Settings, template);

        if (!plan.Active)
            return new StartOutcome(false, null, new Msg("本场没有开启翻译。", "Translation is not on for this service."));

        // Refused rather than attempted when there is nothing for the translator to feed. Starting
        // it here used to answer "translation reconnected" and then be stopped by the background
        // reconciler on its next pass — a button that reported success and quietly undid itself, in
        // the one state it is shown for. Saying what is actually wrong is worth more than a retry
        // that cannot work: the translated audio has nowhere to go until the second RTMP target is
        // pushing again, and that is fixed in OBS, not here.
        var translated = _state.Read(s => s.Translated);

        if (translated?.Id is null)
            return new StartOutcome(false, null, new Msg(
                "本场没有建出翻译直播（开播时那一步是警告状态），所以没有可以重连的目标。" +
                "本场只能是原声；下一场开播前请让管理员检查设置页的翻译配置。",
                "No translated broadcast was created for this service — that step warned during the start — " +
                "so there is nothing to reconnect to. This service is original audio only; ask the " +
                "administrator to check the translation settings before the next one."));

        if (translated.Status == BroadcastStatus.Complete)
            return new StartOutcome(false, null, new Msg(
                "翻译那条直播已经被 YouTube 结束了，重连翻译不会让它回来 —— 译音没有地方可去。" +
                "请在 OBS 的「多路推流」面板确认第二个目标还在推流；原声这条不受影响。",
                "YouTube has already ended the translated broadcast, so reconnecting the translator cannot " +
                "bring it back — the translated audio has nowhere to go. Check in the OBS multi-RTMP dock " +
                "that the second target is still streaming. The primary broadcast is unaffected."));

        var problem = await _translation.StartAsync(plan.TargetLanguage, ct).ConfigureAwait(false);
        if (problem is not null) return new StartOutcome(false, null, problem);

        _state.RecordAction(new Msg("重启 AI 翻译", "Restarted AI translation"), today?.Title);
        return new StartOutcome(true, null, new Msg("翻译已重新连接。", "Translation reconnected."));
    }

    // ---------------------------------------------------------------- helpers

    private BroadcastState RequireBroadcast() =>
        _state.Read(s => s.Broadcast) is { Id: not null } broadcast
            ? broadcast
            : throw new LocalizedInvalidOperationException(new Msg(
                "还没有创建直播，请从第 1 步重试。",
                "No broadcast has been created yet. Retry from step 1."));

    private void SetStep(int step, string status, Msg? message) => _state.Mutate(s =>
    {
        var entry = s.Steps.FirstOrDefault(x => x.Step == step);
        if (entry is null)
        {
            entry = new StepState { Step = step, Name = StepNames.First(n => n.Step == step).Name };
            s.Steps.Add(entry);
        }
        entry.Status = status;
        entry.Message = message;
    });

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };
}
