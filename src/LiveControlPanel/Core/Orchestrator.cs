using LiveControlPanel.Config;
using LiveControlPanel.Obs;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Core;

public sealed record StartOutcome(bool Ok, int? FailedStep, Msg Message);

/// <summary>
/// The six-step start-today sequence of FR 4.2.
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
/// </summary>
public sealed class Orchestrator
{
    public const int StepCreate = 1;
    public const int StepBind = 2;
    public const int StepThumbnail = 3;
    public const int StepScene = 4;
    public const int StepStream = 5;
    public const int StepAwaitLive = 6;

    private static readonly (int Step, Msg Name)[] StepNames =
    {
        (StepCreate, new Msg("创建直播", "Create broadcast")),
        (StepBind, new Msg("绑定推流密钥", "Bind stream key")),
        (StepThumbnail, new Msg("上传封面", "Upload thumbnail")),
        (StepScene, new Msg("切换画面", "Switch scene")),
        (StepStream, new Msg("开始推流", "Start streaming")),
        (StepAwaitLive, new Msg("等待 YouTube 上线", "Wait for YouTube to go live")),
    };

    private static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LivePollTimeout = TimeSpan.FromSeconds(60);

    private readonly ConfigStore _config;
    private readonly StateManager _state;
    private readonly IYouTubeClient _youtube;
    private readonly IObsClient _obs;
    private readonly ILogger<Orchestrator> _log;

    /// <summary>Serializes runs. Taken with a zero timeout so a concurrent tap is rejected, not queued.</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public Orchestrator(
        ConfigStore config,
        StateManager state,
        IYouTubeClient youtube,
        IObsClient obs,
        ILogger<Orchestrator> log)
    {
        _config = config;
        _state = state;
        _youtube = youtube;
        _obs = obs;
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

                // Only rebuilt for a run from the beginning. A retry resumes at fromStep and never
                // executes the steps before it, so resetting them left them "pending" for good —
                // and the progress card stays on screen for the whole Live phase, so a successful
                // recovery left the operator watching four grey dots for the rest of the service,
                // on precisely the path where they most need to believe what the panel says.
                if (fromStep <= StepCreate || s.Steps.Count == 0)
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

        for (var step = Math.Max(StepCreate, fromStep); step <= StepAwaitLive; step++)
        {
            SetStep(step, "running", null);

            try
            {
                var message = await RunStepAsync(step, today, template, ct).ConfigureAwait(false);
                SetStep(step, message.Skipped ? "skipped" : "done", message.Text);
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

    private sealed record StepMessage(Msg? Text, bool Skipped = false);

    private async Task<StepMessage> RunStepAsync(
        int step, TodayState today, ServiceTemplate? template, CancellationToken ct) => step switch
    {
        StepCreate => await CreateAsync(today, template, ct).ConfigureAwait(false),
        StepBind => await BindAsync(ct).ConfigureAwait(false),
        StepThumbnail => await ThumbnailAsync(template, ct).ConfigureAwait(false),
        StepScene => await SceneAsync(ct).ConfigureAwait(false),
        StepStream => await StreamAsync(ct).ConfigureAwait(false),
        StepAwaitLive => await AwaitLiveAsync(ct).ConfigureAwait(false),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown orchestration step."),
    };

    // ---------------------------------------------------------------- steps

    private async Task<StepMessage> CreateAsync(TodayState today, ServiceTemplate? template, CancellationToken ct)
    {
        // Idempotency anchor: a broadcast already exists, so never insert a second one.
        var existing = _state.Read(s => s.Broadcast);
        if (existing?.Id is not null) return new StepMessage(new Msg($"已存在直播 {existing.Id}", $"Broadcast {existing.Id} already exists"), Skipped: true);

        // Second anchor, on YouTube's side: if a previous attempt's insert succeeded but the
        // response was lost (timeout mid-create), the local state is empty while the broadcast
        // exists. Titles carry the date, so an unfinished broadcast with today's exact title is
        // that lost attempt — adopt it instead of creating a duplicate.
        try
        {
            // Same title AND created today. Titles carry the date only because the shipped
            // TitleFormat happens to contain {M}/{D}/{YYYY} — an ad-hoc broadcast with a hand-typed
            // title, or an edited format, breaks that silently. Without the date test the panel
            // would adopt an unfinished broadcast from a previous week and stream today's service
            // into last week's watch URL, which is also the URL Telegram then distributes.
            var leftover = (await _youtube.ListUnfinishedBroadcastsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(b => b.Title == today.Title && IsFromToday(b));
            if (leftover is not null)
            {
                // The adopted broadcast's real lifecycle, not a flat "created".
                //
                // Runtime state is memory-only by design, so a panel restart mid-service loses the
                // broadcast; the phase then reads Ready, the operator taps start, and this adopts a
                // broadcast that is already live. Recording it as "created" sent step 2 on to bind a
                // live broadcast, which YouTube rejects — and every retry of that step failed the
                // same way, with no path forward that did not end the service.
                _state.Mutate(s => s.Broadcast = new BroadcastState
                {
                    Id = leftover.Id,
                    WatchUrl = leftover.WatchUrl,
                    Status = AdoptedStatus(leftover.LifeCycleStatus),
                    Title = leftover.Title,
                    CreatedOn = _state.Clock(),
                });
                _log.LogInformation("Adopted existing broadcast {Id} \"{Title}\" instead of creating a duplicate",
                    leftover.Id, leftover.Title);
                return new StepMessage(new Msg($"沿用已创建的 {leftover.Id}", $"Reusing existing {leftover.Id}"));
            }
        }
        catch (Exception ex)
        {
            // The check is best-effort: if the listing itself fails, creating is still the right
            // move — worst case is the duplicate the pre-flight already knows how to clean up.
            _log.LogDebug(ex, "Checking for an existing broadcast before create failed");
        }

        var settings = _config.Settings;
        var description = Coalesce(today.Description,
            Coalesce(template?.Description, settings.DefaultDescription));

        var info = await _youtube.CreateBroadcastAsync(new CreateBroadcastRequest(
            Title: today.Title!,
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

        _state.Mutate(s => s.Broadcast = new BroadcastState
        {
            Id = info.Id,
            WatchUrl = info.WatchUrl,
            Status = BroadcastStatus.Created,
            Title = info.Title,
            CreatedOn = _state.Clock(),
        });

        return new StepMessage(new Msg($"已创建 {info.Id}", $"Created {info.Id}"));
    }

    private async Task<StepMessage> BindAsync(CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        if (broadcast.Status is not BroadcastStatus.Created)
            return new StepMessage(new Msg("已绑定", "Already bound"), Skipped: true);

        var streamId = _config.Settings.StreamId;
        if (string.IsNullOrWhiteSpace(streamId))
            throw new LocalizedInvalidOperationException(new Msg(
                "还没有创建推流密钥。请让管理员在设置页点击「创建推流密钥」，并把密钥填进 OBS。",
                "No stream key has been created yet. Ask the administrator to create one on the settings " +
                "page and enter it in OBS."));

        await _youtube.BindStreamAsync(broadcast.Id!, streamId, ct).ConfigureAwait(false);
        _state.Mutate(s => { if (s.Broadcast is not null) s.Broadcast.Status = BroadcastStatus.Bound; });
        return new StepMessage(new Msg("已绑定推流密钥", "Stream key bound"));
    }

    private async Task<StepMessage> ThumbnailAsync(ServiceTemplate? template, CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        if (broadcast.ThumbnailUploaded) return new StepMessage(new Msg("封面已上传", "Thumbnail already uploaded"), Skipped: true);

        var relative = Coalesce(template?.ThumbnailFile, _config.Settings.DefaultThumbnail);
        if (string.IsNullOrWhiteSpace(relative)) return new StepMessage(new Msg("未配置封面", "No thumbnail configured"), Skipped: true);

        var path = _config.Paths.Resolve(relative);
        if (!File.Exists(path))
        {
            // A missing thumbnail is cosmetic; refusing to go live over it would be absurd.
            _log.LogWarning("Thumbnail {Path} not found; skipping upload", path);
            return new StepMessage(new Msg("找不到封面文件，已跳过", "Thumbnail file not found; skipped"), Skipped: true);
        }

        await using var stream = File.OpenRead(path);
        await _youtube.SetThumbnailAsync(broadcast.Id!, stream, ContentType(path), ct).ConfigureAwait(false);
        _state.Mutate(s => { if (s.Broadcast is not null) s.Broadcast.ThumbnailUploaded = true; });
        return new StepMessage(new Msg("封面已上传", "Thumbnail uploaded"));
    }

    private async Task<StepMessage> SceneAsync(CancellationToken ct)
    {
        var scene = _config.Settings.Obs.SceneCamera;
        if (string.IsNullOrWhiteSpace(scene)) return new StepMessage(new Msg("未配置起始画面", "No starting scene configured"), Skipped: true);

        if (string.Equals(_obs.Status.CurrentScene, scene, StringComparison.Ordinal))
            return new StepMessage(new Msg($"已在「{scene}」", $"Already on \"{scene}\""), Skipped: true);

        await _obs.SetSceneAsync(scene, ct).ConfigureAwait(false);
        return new StepMessage(new Msg($"已切到「{scene}」", $"Switched to \"{scene}\""));
    }

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
    /// </summary>
    private async Task<StepMessage> AwaitLiveAsync(CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        if (broadcast.Status == BroadcastStatus.Live) return new StepMessage(new Msg("已上线", "Already live"), Skipped: true);

        var deadline = DateTime.UtcNow + LivePollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var status = await _youtube.GetLifeCycleStatusAsync(broadcast.Id!, ct).ConfigureAwait(false);

            if (status == "live")
            {
                _state.Mutate(s => { if (s.Broadcast is not null) s.Broadcast.Status = BroadcastStatus.Live; });
                return new StepMessage(new Msg("YouTube 已上线", "YouTube is live"));
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
                _state.Mutate(s => { if (s.Broadcast is not null) s.Broadcast.Status = BroadcastStatus.Testing; });

            await Task.Delay(LivePollInterval, ct).ConfigureAwait(false);
        }

        throw new LocalizedTimeoutException(new Msg(
            "YouTube 还没有确认收到画面。推流可能仍在建立，请稍等十几秒后点「重试这一步」；若持续如此，请检查网络。",
            "YouTube has not confirmed it is receiving video yet. The stream may still be establishing — " +
            "wait about fifteen seconds and retry this step; if it persists, check the network."));
    }

    // ---------------------------------------------------------------- stop / cleanup

    /// <summary>FR 4.3. OBS stops sending first, then the broadcast is transitioned to complete.</summary>
    public async Task<StartOutcome> StopAsync(CancellationToken ct = default)
    {
        var broadcast = _state.Read(s => s.Broadcast);

        // "Not streaming" and "cannot see OBS" are different answers, and only one of them means
        // there is nothing to stop. ObsClient publishes Streaming = false whenever the websocket
        // drops, so when the connection is down this test used to skip OBS entirely, transition the
        // broadcast to complete, and report "the broadcast has ended" — while OBS carried on
        // encoding and uploading to the ingest for the rest of the day.
        if (!_obs.Status.Connected)
        {
            _log.LogWarning("Stop requested while OBS is not connected; not claiming the stream stopped");
            return new StartOutcome(false, null, new Msg(
                "面板连不上 OBS，没法确认推流已经停止。请直接在 OBS 里点「停止推流」，" +
                "然后再回到本页结束直播 —— 在那之前这场还没有真正结束。",
                "The panel cannot reach OBS, so it cannot confirm that streaming has stopped. Stop it " +
                "directly in OBS, then come back here to end the broadcast — until then this service has " +
                "not actually finished."));
        }

        try
        {
            if (_obs.Status.Streaming) await _obs.StopStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stopping the OBS stream failed");
            return new StartOutcome(false, null, new Msg(
                "无法让 OBS 停止推流。请直接在 OBS 里点「停止推流」，然后再回到本页结束直播。",
                "OBS would not stop streaming. Stop it directly in OBS, then come back here to end the broadcast."));
        }

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
                _state.RecordAction(new Msg("停止直播（YouTube 未确认）", "Stopped (YouTube unconfirmed)"), broadcast.Title);
                return new StartOutcome(false, null, new Msg(
                    "推流已停止，但 YouTube 那边没有确认结束。请稍后在 YouTube Studio 里确认这场已结束。",
                    "Streaming has stopped, but YouTube did not confirm the broadcast ended. Check in " +
                    "YouTube Studio later that it shows as finished."));
            }

            _state.Mutate(s => { if (s.Broadcast is not null) s.Broadcast.Status = BroadcastStatus.Complete; });
        }

        _state.RecordAction(new Msg("停止直播", "Stopped the broadcast"), broadcast?.Title);
        return new StartOutcome(true, null, new Msg("直播已结束。", "The broadcast has ended."));
    }

    /// <summary>FR 4.4 one-click fix for the leftover-broadcast case.</summary>
    public async Task<StartOutcome> EndPreviousAsync(CancellationToken ct = default)
    {
        try
        {
            var unfinished = await _youtube.ListUnfinishedBroadcastsAsync(ct).ConfigureAwait(false);
            var current = _state.Read(s => s.Broadcast?.Id);

            var ended = 0;
            foreach (var broadcast in unfinished)
            {
                if (broadcast.Id == current) continue;

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
            s.Today = null;
            s.Steps = new List<StepState>();
            s.Telegram = new TelegramState();
        });
        return true;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Maps YouTube's lifeCycleStatus onto the panel's own. Anything already on air adopts as Live
    /// so bind, thumbnail and await-live all skip themselves; anything bound but not yet started
    /// adopts as Bound so bind is not attempted twice.
    /// </summary>
    /// <summary>
    /// Whether a leftover was created today, by the panel's own clock.
    ///
    /// A broadcast still on air from earlier today is adoptable; one from last Wednesday is a
    /// leftover for the pre-flight to clean up, never something to resume into.
    /// </summary>
    private bool IsFromToday(BroadcastInfo broadcast) =>
        broadcast.CreatedAt is not { } created || created.ToLocalTime().Date == _state.Clock().Date;

    private static string AdoptedStatus(string? lifeCycleStatus) => lifeCycleStatus switch
    {
        "live" or "liveStarting" => BroadcastStatus.Live,
        "testing" or "testStarting" => BroadcastStatus.Testing,
        "ready" => BroadcastStatus.Bound,
        _ => BroadcastStatus.Created,
    };

    /*
     * Every mutation of s.Broadcast above is null-guarded rather than null-forgiving.
     *
     * RefreshScheduleLocked can set s.Broadcast = null on the day rollover, which is reachable for a
     * service that crosses midnight and has not started streaming — status still Created or Bound,
     * Obs.Streaming false. The NRE would be thrown inside Mutate while the state lock is held,
     * aborting the mutation and skipping the push to every connected page.
     */
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
