using LiveControlPanel.Config;
using LiveControlPanel.Obs;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Core;

/// <summary>
/// The five pre-start checks of FR 4.4.
///
/// Two rules shape everything here:
/// a failing check never blocks going live — in an emergency the stream matters more than the
/// checklist — and every message tells a non-technical operator what to do, because at 04:40 there
/// is nobody to ask.
/// </summary>
public sealed class Preflight
{
    /// <summary>Audio level readings older than this are treated as "no signal".</summary>
    private static readonly TimeSpan AudioWindow = TimeSpan.FromSeconds(5);

    /// <summary>Peak (0..1) above which we consider the mixer to be producing sound.</summary>
    private const double AudioActivityThreshold = 0.0005;

    private readonly ConfigStore _config;
    private readonly IObsClient _obs;
    private readonly IYouTubeClient _youtube;
    private readonly ILogger<Preflight> _log;

    public Preflight(ConfigStore config, IObsClient obs, IYouTubeClient youtube, ILogger<Preflight> log)
    {
        _config = config;
        _obs = obs;
        _youtube = youtube;
        _log = log;
    }

    public async Task<List<PreflightItem>> RunAsync(CancellationToken ct = default)
    {
        var items = new List<PreflightItem>
        {
            CheckObs(),
            await CheckAudioAsync(ct).ConfigureAwait(false),
            await CheckPreviousBroadcastAsync(ct).ConfigureAwait(false),
            await CheckAuthAsync(ct).ConfigureAwait(false),
            await CheckVideoAsync(ct).ConfigureAwait(false),
        };

        return items;
    }

    private PreflightItem CheckObs()
    {
        if (_obs.Status.Connected)
            return new PreflightItem { Key = "obs", Ok = true, Message = "OBS 已连接。" };

        return new PreflightItem
        {
            Key = "obs",
            Ok = false,
            Message = "OBS 没有连上。请确认 OBS Studio 已经打开；打开后本页会自动恢复，无需刷新。",
        };
    }

    private async Task<PreflightItem> CheckAudioAsync(CancellationToken ct)
    {
        var name = _config.Settings.Obs.AudioInputName;

        if (string.IsNullOrWhiteSpace(name))
            return new PreflightItem { Key = "audio", Ok = true, Message = "未配置音频输入名称，已跳过检查。" };

        if (!_obs.Status.Connected)
            return new PreflightItem
            {
                Key = "audio",
                Ok = false,
                Message = "无法检查声音，因为 OBS 没有连上。请先打开 OBS。",
            };

        IReadOnlyList<string> inputs;
        try
        {
            inputs = await _obs.GetInputNamesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Listing OBS inputs failed");
            inputs = Array.Empty<string>();
        }

        var exists = inputs.Any(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            return new PreflightItem
            {
                Key = "audio",
                Ok = false,
                Message = $"OBS 里找不到名为「{name}」的声音设备。请检查调音台是否开机、USB 线是否插好；" +
                          "若换过设备，请让管理员在设置页更新名称。",
            };

        var peak = _obs.GetRecentAudioPeak(name, AudioWindow);

        if (peak is null)
            return new PreflightItem
            {
                Key = "audio",
                Ok = false,
                Message = $"声音设备「{name}」存在，但读不到音量。请确认调音台已开机、USB 线已插好，" +
                          "并让人在麦克风前说句话再看这里。",
            };

        if (peak.Value < AudioActivityThreshold)
            return new PreflightItem
            {
                Key = "audio",
                Ok = false,
                Message = $"声音设备「{name}」没有声音。请检查调音台是否开机、推子是否推起来、USB 线是否插好。",
            };

        return new PreflightItem { Key = "audio", Ok = true, Message = $"声音正常（{name}）。" };
    }

    /// <summary>
    /// FR 4.4's highest-risk item. Wednesday and Friday run two services a day and share one stream
    /// key; if the morning operator forgot to end their broadcast, the evening operator hits YouTube's
    /// one-broadcast-per-key limit. This must be answered *before* the start button is pressed, and it
    /// must offer a one-click fix rather than surfacing an API error.
    /// </summary>
    private async Task<PreflightItem> CheckPreviousBroadcastAsync(CancellationToken ct)
    {
        try
        {
            var unfinished = await _youtube.ListUnfinishedBroadcastsAsync(ct).ConfigureAwait(false);
            if (unfinished.Count == 0)
                return new PreflightItem { Key = "previousBroadcast", Ok = true, Message = "没有未结束的直播。" };

            var titles = string.Join("、", unfinished.Select(b => $"「{b.Title}」"));
            return new PreflightItem
            {
                Key = "previousBroadcast",
                Ok = false,
                Message = $"上一场直播{titles}仍在进行，需要先结束它才能开始新的一场。是否现在结束？",
                Action = "end-previous",
            };
        }
        catch (NotAuthorizedException)
        {
            return new PreflightItem
            {
                Key = "previousBroadcast",
                Ok = false,
                Message = "还没有授权 YouTube 账号，无法检查上一场直播。请先完成授权。",
                Action = "reauthorize",
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking for unfinished broadcasts failed");
            return new PreflightItem
            {
                Key = "previousBroadcast",
                Ok = false,
                Message = "暂时查不到 YouTube 上是否有未结束的直播，请检查网络后重试自检。",
            };
        }
    }

    private async Task<PreflightItem> CheckAuthAsync(CancellationToken ct)
    {
        try
        {
            var info = await _youtube.GetAuthInfoAsync(ct).ConfigureAwait(false);

            if (!info.Valid)
                return new PreflightItem
                {
                    Key = "auth",
                    Ok = false,
                    Message = info.Message ?? "YouTube 授权已失效，需要重新授权。",
                    Action = "reauthorize",
                };

            // Warn early. FR 8: the token must not quietly die at 04:40 with nobody to fix it.
            if (info.ExpiresInDays is <= 14)
                return new PreflightItem
                {
                    Key = "auth",
                    Ok = false,
                    Message = $"YouTube 授权还有 {info.ExpiresInDays} 天到期，请尽快请管理员重新授权。",
                    Action = "reauthorize",
                };

            var suffix = info.ExpiresInDays is null ? "" : $"（剩余约 {info.ExpiresInDays} 天）";
            return new PreflightItem { Key = "auth", Ok = true, Message = $"YouTube 授权有效{suffix}。" };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking YouTube authorization failed");
            return new PreflightItem
            {
                Key = "auth",
                Ok = false,
                Message = "无法确认 YouTube 授权状态，请检查网络后重试自检。",
                Action = "reauthorize",
            };
        }
    }

    private async Task<PreflightItem> CheckVideoAsync(CancellationToken ct)
    {
        var sources = _config.Settings.Obs.VideoSourceNames;

        if (sources.Count == 0)
            return new PreflightItem
            {
                Key = "video",
                Ok = true,
                Message = "未配置画面来源名称，已跳过检查。可在设置页填写采集卡与电视采集源的名称。",
            };

        if (!_obs.Status.Connected)
            return new PreflightItem
            {
                Key = "video",
                Ok = false,
                Message = "无法检查画面，因为 OBS 没有连上。请先打开 OBS。",
            };

        var dead = new List<string>();
        var unknown = new List<string>();

        foreach (var source in sources)
        {
            var active = await _obs.IsSourceActiveAsync(source, ct).ConfigureAwait(false);
            if (active is null) unknown.Add(source);
            else if (!active.Value) dead.Add(source);
        }

        if (dead.Count > 0)
            return new PreflightItem
            {
                Key = "video",
                Ok = false,
                Message = $"画面来源{string.Join("、", dead.Select(d => $"「{d}」"))}没有图像。" +
                          "请检查摄像机是否开机、电视是否打开、采集卡的线是否插好。",
            };

        if (unknown.Count > 0)
            return new PreflightItem
            {
                Key = "video",
                Ok = false,
                Message = $"在 OBS 里找不到画面来源{string.Join("、", unknown.Select(u => $"「{u}」"))}。" +
                          "请让管理员在设置页核对名称。",
            };

        return new PreflightItem { Key = "video", Ok = true, Message = "画面正常。" };
    }
}
