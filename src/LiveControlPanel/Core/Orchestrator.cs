using LiveControlPanel.Config;
using LiveControlPanel.Obs;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Core;

public sealed record StartOutcome(bool Ok, int? FailedStep, string Message);

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

    private static readonly (int Step, string Name)[] StepNames =
    {
        (StepCreate, "创建直播"),
        (StepBind, "绑定推流密钥"),
        (StepThumbnail, "上传封面"),
        (StepScene, "切换画面"),
        (StepStream, "开始推流"),
        (StepAwaitLive, "等待 YouTube 上线"),
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
            return new StartOutcome(true, null, "正在开始直播，请稍候…");
        }

        try
        {
            var today = _state.Read(s => s.Today);
            if (today?.Title is null)
                return new StartOutcome(false, null, "今天没有排期。请先在「不是这一场？」里选择要开始的场次。");

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
                _state.RecordAction($"开播失败（第 {step} 步）", today.Title);
                return new StartOutcome(false, step, friendly);
            }
        }

        _state.RecordAction("开始直播", today.Title);
        return new StartOutcome(true, null, "直播已开始。");
    }

    private sealed record StepMessage(string? Text, bool Skipped = false);

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
        if (existing?.Id is not null) return new StepMessage($"已存在直播 {existing.Id}", Skipped: true);

        var settings = _config.Settings;
        var description = Coalesce(today.Description,
            Coalesce(template?.Description, settings.DefaultDescription));

        var info = await _youtube.CreateBroadcastAsync(new CreateBroadcastRequest(
            Title: today.Title!,
            Description: description,
            ScheduledStart: today.ScheduledStart ?? DateTime.Now,
            PrivacyStatus: Coalesce(template?.PrivacyStatus, "unlisted"),
            MadeForKids: template?.MadeForKids ?? false,
            LatencyPreference: Coalesce(template?.LatencyPreference, "ultraLow")), ct).ConfigureAwait(false);

        _state.Mutate(s => s.Broadcast = new BroadcastState
        {
            Id = info.Id,
            WatchUrl = info.WatchUrl,
            Status = BroadcastStatus.Created,
            Title = info.Title,
        });

        return new StepMessage($"已创建 {info.Id}");
    }

    private async Task<StepMessage> BindAsync(CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        if (broadcast.Status is not BroadcastStatus.Created)
            return new StepMessage("已绑定", Skipped: true);

        var streamId = _config.Settings.StreamId;
        if (string.IsNullOrWhiteSpace(streamId))
            throw new InvalidOperationException(
                "还没有创建推流密钥。请让管理员在设置页点击「创建推流密钥」，并把密钥填进 OBS。");

        await _youtube.BindStreamAsync(broadcast.Id!, streamId, ct).ConfigureAwait(false);
        _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Bound);
        return new StepMessage("已绑定推流密钥");
    }

    private async Task<StepMessage> ThumbnailAsync(ServiceTemplate? template, CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        if (broadcast.ThumbnailUploaded) return new StepMessage("封面已上传", Skipped: true);

        var relative = Coalesce(template?.ThumbnailFile, _config.Settings.DefaultThumbnail);
        if (string.IsNullOrWhiteSpace(relative)) return new StepMessage("未配置封面", Skipped: true);

        var path = _config.Paths.Resolve(relative);
        if (!File.Exists(path))
        {
            // A missing thumbnail is cosmetic; refusing to go live over it would be absurd.
            _log.LogWarning("Thumbnail {Path} not found; skipping upload", path);
            return new StepMessage("找不到封面文件，已跳过", Skipped: true);
        }

        await using var stream = File.OpenRead(path);
        await _youtube.SetThumbnailAsync(broadcast.Id!, stream, ContentType(path), ct).ConfigureAwait(false);
        _state.Mutate(s => s.Broadcast!.ThumbnailUploaded = true);
        return new StepMessage("封面已上传");
    }

    private async Task<StepMessage> SceneAsync(CancellationToken ct)
    {
        var scene = _config.Settings.Obs.SceneCamera;
        if (string.IsNullOrWhiteSpace(scene)) return new StepMessage("未配置起始画面", Skipped: true);

        if (string.Equals(_obs.Status.CurrentScene, scene, StringComparison.Ordinal))
            return new StepMessage($"已在「{scene}」", Skipped: true);

        await _obs.SetSceneAsync(scene, ct).ConfigureAwait(false);
        return new StepMessage($"已切到「{scene}」");
    }

    private async Task<StepMessage> StreamAsync(CancellationToken ct)
    {
        // Idempotency: OBS is already sending, so do not restart the output.
        if (_obs.Status.Streaming) return new StepMessage("已在推流", Skipped: true);

        await _obs.StartStreamAsync(ct).ConfigureAwait(false);
        return new StepMessage("已开始推流");
    }

    /// <summary>
    /// Polls until YouTube reports the broadcast live. enableAutoStart makes the transition automatic
    /// once frames arrive; this only waits for it so the operator knows the link really works.
    /// </summary>
    private async Task<StepMessage> AwaitLiveAsync(CancellationToken ct)
    {
        var broadcast = RequireBroadcast();
        if (broadcast.Status == BroadcastStatus.Live) return new StepMessage("已上线", Skipped: true);

        var deadline = DateTime.UtcNow + LivePollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var status = await _youtube.GetLifeCycleStatusAsync(broadcast.Id!, ct).ConfigureAwait(false);

            if (status == "live")
            {
                _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Live);
                return new StepMessage("YouTube 已上线");
            }

            if (status == "testing")
                _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Testing);

            await Task.Delay(LivePollInterval, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "YouTube 还没有确认收到画面。推流可能仍在建立，请稍等十几秒后点「重试这一步」；" +
            "若持续如此，请检查网络。");
    }

    // ---------------------------------------------------------------- stop / cleanup

    /// <summary>FR 4.3. OBS stops sending first, then the broadcast is transitioned to complete.</summary>
    public async Task<StartOutcome> StopAsync(CancellationToken ct = default)
    {
        var broadcast = _state.Read(s => s.Broadcast);

        try
        {
            if (_obs.Status.Streaming) await _obs.StopStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stopping the OBS stream failed");
            return new StartOutcome(false, null,
                "无法让 OBS 停止推流。请直接在 OBS 里点「停止推流」，然后再回到本页结束直播。");
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
                _state.RecordAction("停止直播（YouTube 未确认）", broadcast.Title);
                return new StartOutcome(false, null,
                    "推流已停止，但 YouTube 那边没有确认结束。请稍后在 YouTube Studio 里确认这场已结束。");
            }

            _state.Mutate(s => s.Broadcast!.Status = BroadcastStatus.Complete);
        }

        _state.RecordAction("停止直播", broadcast?.Title);
        return new StartOutcome(true, null, "直播已结束。");
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
                await _youtube.TransitionToCompleteAsync(broadcast.Id, ct).ConfigureAwait(false);
                ended++;
            }

            _state.RecordAction($"结束遗留直播 {ended} 场");
            return ended == 0
                ? new StartOutcome(true, null, "没有需要结束的直播。")
                : new StartOutcome(true, null, $"已结束 {ended} 场未完成的直播，现在可以开始今天的直播了。");
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

    private BroadcastState RequireBroadcast() =>
        _state.Read(s => s.Broadcast) is { Id: not null } broadcast
            ? broadcast
            : throw new InvalidOperationException("还没有创建直播，请从第 1 步重试。");

    private void SetStep(int step, string status, string? message) => _state.Mutate(s =>
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
