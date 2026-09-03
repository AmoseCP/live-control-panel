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

    /// <summary>
    /// Gap between the two frames compared for the frozen-picture check.
    ///
    /// Paid once per pre-flight, not once per source: every first frame is taken, then one wait,
    /// then every second frame. A second and a half is many frames at any sane frame rate, so a
    /// live camera cannot produce two identical ones across it.
    /// </summary>
    internal TimeSpan FreezeSampleGap { get; set; } = TimeSpan.FromMilliseconds(1500);

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

    /// <summary>
    /// "OBS is not connected" on its own sends an operator to check the one thing that is usually
    /// already right. Opening OBS is not enough: obs-websocket ships disabled, so a running OBS with
    /// the server switched off looks exactly like a closed one. Name the actual fix per cause.
    /// </summary>
    private PreflightItem CheckObs()
    {
        if (_obs.Status.Connected)
            return Ok("obs", ("OBS 已连接。", "OBS is connected."));

        return _obs.Problem switch
        {
            ObsProblem.NotListening => Fail("obs", (
                "连不上 OBS。如果 OBS 已经打开，请在 OBS 里点 工具 → WebSocket 服务器设置 → " +
                "勾选「启用 WebSocket 服务器」（这一项默认是关的）。若 OBS 没开，打开即可 —— " +
                "本页会自动恢复，无需刷新。",
                "Cannot reach OBS. If OBS is already open, go to Tools → WebSocket Server Settings in " +
                "OBS and tick \"Enable WebSocket server\" — it is off by default. If OBS is closed, just " +
                "open it; this page recovers on its own, no need to refresh.")),

            ObsProblem.AuthenticationFailed => Fail("obs", (
                "OBS 拒绝了密码。请在 OBS 里点 工具 → WebSocket 服务器设置 → 显示连接信息，" +
                "把密码复制到本面板设置页的「WebSocket 密码」。",
                "OBS rejected the password. In OBS, go to Tools → WebSocket Server Settings → Show " +
                "Connect Info and copy the password into \"WebSocket password\" on the settings page.")),

            ObsProblem.BadUrl => Fail("obs", (
                $"OBS 连接地址填错了（{_config.Settings.Obs.Url}）。请在设置页改回 ws://localhost:4455。",
                $"The OBS address is not valid ({_config.Settings.Obs.Url}). Set it back to " +
                "ws://localhost:4455 on the settings page.")),

            _ => Fail("obs", (
                "OBS 没有连上。请确认 OBS Studio 已经打开，且 工具 → WebSocket 服务器设置 里已启用服务器；" +
                "打开后本页会自动恢复，无需刷新。",
                "OBS is not connected. Check that OBS Studio is open and that its WebSocket server is " +
                "enabled under Tools → WebSocket Server Settings; this page recovers on its own.")),
        };
    }

    private async Task<PreflightItem> CheckAudioAsync(CancellationToken ct)
    {
        var name = _config.Settings.Obs.AudioInputName;

        if (string.IsNullOrWhiteSpace(name))
            return Ok("audio", ("未配置音频输入名称，已跳过检查。",
                "No audio input name configured; check skipped."));

        if (!_obs.Status.Connected)
            return Fail("audio", (
                "无法检查声音，因为 OBS 没有连上。请先打开 OBS。",
                "Cannot check audio because OBS is not connected. Open OBS first."));

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
            return Fail("audio", (
                $"OBS 里找不到名为「{name}」的声音设备。请检查调音台是否开机、USB 线是否插好；" +
                "若换过设备，请让管理员在设置页更新名称。",
                $"OBS has no audio device called \"{name}\". Check that the mixer is powered on and the " +
                "USB cable is connected; if the device changed, ask the administrator to update the name " +
                "on the settings page."));

        var peak = _obs.GetRecentAudioPeak(name, AudioWindow);

        if (peak is null)
            return Fail("audio", (
                $"声音设备「{name}」存在，但读不到音量。请确认调音台已开机、USB 线已插好，" +
                "并让人在麦克风前说句话再看这里。",
                $"Audio device \"{name}\" is there, but no level is coming through. Check that the mixer " +
                "is on and the USB cable is connected, then have someone speak into a microphone and " +
                "look again."));

        if (peak.Value < AudioActivityThreshold)
            return Fail("audio", (
                $"声音设备「{name}」没有声音。请检查调音台是否开机、推子是否推起来、USB 线是否插好。",
                $"Audio device \"{name}\" is silent. Check that the mixer is on, the faders are up, and " +
                "the USB cable is connected."));

        return Ok("audio", ($"声音正常（{name}）。", $"Audio is fine ({name})."));
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
                return Ok("previousBroadcast", ("没有未结束的直播。", "No unfinished broadcasts."));

            // Name a few, count the rest. A channel with years of manual streaming can hold dozens
            // of leftovers — enumerating every title turned this warning into a wall of text, and
            // the one-click fix ends them all regardless of how many are named.
            const int named = 3;
            var zhTitles = string.Join("、", unfinished.Take(named).Select(b => $"「{b.Title}」"));
            var enTitles = string.Join(", ", unfinished.Take(named).Select(b => $"\"{b.Title}\""));
            if (unfinished.Count > named)
            {
                zhTitles += $" 等 {unfinished.Count} 场";
                enTitles += $" and {unfinished.Count - named} more";
            }

            return new PreflightItem
            {
                Key = "previousBroadcast",
                Ok = false,
                Message = new Msg(
                    $"上一场直播{zhTitles}仍在进行，需要先结束它才能开始新的一场。是否现在结束？",
                    $"An earlier broadcast ({enTitles}) is still running. It has to be ended before a new " +
                    "one can start. End it now?"),
                Action = "end-previous",
            };
        }
        catch (NotAuthorizedException)
        {
            return new PreflightItem
            {
                Key = "previousBroadcast",
                Ok = false,
                Message = new Msg(
                    "还没有授权 YouTube 账号，无法检查上一场直播。请先完成授权。",
                    "The YouTube account is not authorized yet, so earlier broadcasts cannot be checked. " +
                    "Authorize first."),
                Action = "reauthorize",
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking for unfinished broadcasts failed");
            return Fail("previousBroadcast", (
                "暂时查不到 YouTube 上是否有未结束的直播，请检查网络后重试自检。",
                "Cannot reach YouTube to check for unfinished broadcasts. Check the network, then run " +
                "the checks again."));
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
                    Message = info.Message ?? new Msg(
                        "YouTube 授权已失效，需要重新授权。",
                        "YouTube authorization has expired and needs renewing."),
                    Action = "reauthorize",
                };

            // Warn early. FR 8: the token must not quietly die at 04:40 with nobody to fix it.
            if (info.ExpiresInDays is <= 14)
                return new PreflightItem
                {
                    Key = "auth",
                    Ok = false,
                    Message = new Msg(
                        $"YouTube 授权还有 {info.ExpiresInDays} 天到期，请尽快请管理员重新授权。",
                        $"YouTube authorization expires in {info.ExpiresInDays} days. Ask the " +
                        "administrator to renew it soon."),
                    Action = "reauthorize",
                };

            return Ok("auth", (
                info.ExpiresInDays is null ? "YouTube 授权有效。" : $"YouTube 授权有效（剩余约 {info.ExpiresInDays} 天）。",
                info.ExpiresInDays is null
                    ? "YouTube authorization is valid."
                    : $"YouTube authorization is valid (about {info.ExpiresInDays} days left)."));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking YouTube authorization failed");
            return new PreflightItem
            {
                Key = "auth",
                Ok = false,
                Message = new Msg(
                    "无法确认 YouTube 授权状态，请检查网络后重试自检。",
                    "Cannot confirm the YouTube authorization. Check the network, then run the checks again."),
                Action = "reauthorize",
            };
        }
    }

    private async Task<PreflightItem> CheckVideoAsync(CancellationToken ct)
    {
        var sources = _config.Settings.Obs.VideoSourceNames;
        var watched = _config.Settings.Obs.FrozenFrameSourceNames;

        if (sources.Count == 0 && watched.Count == 0)
            return Ok("video", (
                "未配置画面来源名称，已跳过检查。可在设置页填写采集卡与电视采集源的名称。",
                "No video source names configured; check skipped. They can be filled in on the settings page."));

        if (!_obs.Status.Connected)
            return Fail("video", (
                "无法检查画面，因为 OBS 没有连上。请先打开 OBS。",
                "Cannot check video because OBS is not connected. Open OBS first."));

        var dead = new List<string>();
        var unknown = new List<string>();

        foreach (var source in sources)
        {
            var active = await _obs.IsSourceActiveAsync(source, ct).ConfigureAwait(false);
            if (active is null) unknown.Add(source);
            else if (!active.Value) dead.Add(source);
        }

        if (dead.Count > 0)
            return Fail("video", (
                $"画面来源{string.Join("、", dead.Select(d => $"「{d}」"))}没有图像。" +
                "请检查摄像机是否开机、电视是否打开、采集卡的线是否插好。",
                $"Video source{(dead.Count > 1 ? "s" : "")} {string.Join(", ", dead.Select(d => $"\"{d}\""))} " +
                "showing no picture. Check that the camera is on, the TV is on, and the capture card " +
                "cables are connected."));

        if (unknown.Count > 0)
            return Fail("video", (
                $"在 OBS 里找不到画面来源{string.Join("、", unknown.Select(u => $"「{u}」"))}。" +
                "请让管理员在设置页核对名称。",
                $"OBS has no video source called {string.Join(", ", unknown.Select(u => $"\"{u}\""))}. " +
                "Ask the administrator to check the names on the settings page."));

        // Everything above answers "is there a picture at all". This answers "is the picture
        // moving", which is the question the capture card's own "No Signal" placeholder defeats:
        // to OBS that placeholder is a perfectly good picture and the source reports as active.
        var scan = await ScanWatchedSourcesAsync(watched, ct).ConfigureAwait(false);

        // A name OBS does not know is reported, not skipped. Silently skipping it turned a typo in
        // the settings field into a green checklist — the one outcome this check exists to prevent.
        if (scan.Unknown.Count > 0)
            return Fail("video", (
                $"设置里要检查画面是否静止的来源{string.Join("、", scan.Unknown.Select(u => $"「{u}」"))}" +
                "在 OBS 里不存在，这一项没有真正检查。请让管理员在设置页核对名称。",
                $"The source{(scan.Unknown.Count > 1 ? "s" : "")} configured for the frozen-picture check " +
                $"({string.Join(", ", scan.Unknown.Select(u => $"\"{u}\""))}) " +
                "do not exist in OBS, so that check did not actually run. Ask the administrator to check " +
                "the names on the settings page."));

        if (scan.Frozen is { } frozen) return FrozenVideo(frozen);

        return Ok("video", ("画面正常。", "Video is fine."));
    }

    /// <summary>What the frozen-picture pass found: at most one frozen source, plus any bad names.</summary>
    private sealed record WatchedScan(string? Frozen, List<string> Unknown);

    /// <summary>
    /// The first source whose picture has not changed at all, or null.
    ///
    /// Only sources currently on the program output are judged. A source that OBS has deactivated
    /// because no scene is showing it would look frozen for a reason that has nothing to do with the
    /// camera, and a check that cries wolf is worse than no check — the operator learns to click
    /// past the whole checklist.
    /// </summary>
    private async Task<WatchedScan> ScanWatchedSourcesAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var unknown = new List<string>();
        if (names.Count == 0) return new WatchedScan(null, unknown);

        // Existence is checked against OBS's own input list, the way the audio check does it.
        // IsSourceActiveAsync cannot answer this on its own: it returns null both for "OBS has
        // never heard of this" and for "the request failed", and those need opposite treatment.
        IReadOnlyList<string> inputs;
        try
        {
            inputs = await _obs.GetInputNamesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Listing OBS inputs for the frozen-picture check failed");
            inputs = Array.Empty<string>();
        }

        // An empty list means the request failed, not that OBS has no inputs. Calling every watched
        // name unknown on that basis would be a false alarm of exactly the kind this check must not
        // raise, so name-checking is skipped and only the freeze comparison runs.
        var canCheckNames = inputs.Count > 0;

        var first = new List<(string Name, byte[] Frame)>();

        foreach (var name in names)
        {
            if (canCheckNames
                && !inputs.Any(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase)))
            {
                unknown.Add(name);
                continue;
            }

            if (await _obs.IsSourceActiveAsync(name, ct).ConfigureAwait(false) is not true) continue;

            var frame = await SampleAsync(name, ct).ConfigureAwait(false);
            if (frame is not null) first.Add((name, frame));
        }

        if (unknown.Count > 0) return new WatchedScan(null, unknown);

        // Nothing to compare: OBS could not render any of them. "Cannot tell", not "no picture".
        if (first.Count == 0) return new WatchedScan(null, unknown);

        try { await Task.Delay(FreezeSampleGap, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new WatchedScan(null, unknown); }

        foreach (var (name, before) in first)
        {
            var after = await SampleAsync(name, ct).ConfigureAwait(false);
            if (FrameFreeze.LooksFrozen(before, after)) return new WatchedScan(name, unknown);
        }

        return new WatchedScan(null, unknown);
    }

    /// <summary>
    /// One frame, or null for any reason at all.
    ///
    /// The whole pre-flight is wrapped in this posture: a single failed OBS request must never take
    /// the checklist down with it. An operator who taps "run the checks again" and gets a blank
    /// panel has lost the one screen that was going to tell them what is wrong.
    /// </summary>
    private async Task<byte[]?> SampleAsync(string sourceName, CancellationToken ct)
    {
        try
        {
            return await _obs.GetSourceFrameAsync(sourceName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Sampling a frame of {Source} failed", sourceName);
            return null;
        }
    }

    /// <summary>
    /// The message that took the longest to get right, because the obvious advice is wrong: the
    /// operator's instinct is to restart the camera and restart OBS, and on this failure neither
    /// does anything — the capture card is wedged and only re-enumerating the USB device clears it.
    /// So the message says that outright, and offers the one action that helps.
    /// </summary>
    private PreflightItem FrozenVideo(string source)
    {
        var canReset = _config.Settings.CaptureReset.Enabled
                       && !string.IsNullOrWhiteSpace(_config.Settings.CaptureReset.DeviceInstanceId);

        var zh = $"画面来源「{source}」有图像，但一秒多之内完全没有变化 —— 通常是摄像机没信号，" +
                 "或者采集卡卡住了（画面上可能是采集卡自己的 No Signal 提示图）。" +
                 "先看一眼摄像机是否开机、HDMI 是否插在采集卡的 HDMI IN。";

        var en = $"Video source \"{source}\" has a picture, but it did not change at all over more than a " +
                 "second — usually the camera has no signal, or the capture card has wedged (what you are " +
                 "seeing may be the card's own \"No Signal\" screen). First check that the camera is on and " +
                 "that HDMI goes into the card's HDMI IN.";

        zh += canReset
            ? "如果摄像机确实是开着的，点下面的「重置采集卡」—— 重启摄像机和重启 OBS 对这种情况都没用，" +
              "必须让 Windows 重新识别采集卡。"
            : "如果摄像机确实是开着的，需要拔插采集卡的 USB 线或重启电脑 —— 重启摄像机和重启 OBS " +
              "对这种情况都没用，必须让 Windows 重新识别采集卡。";

        en += canReset
            ? " If the camera really is on, use \"reset the capture card\" below: restarting the camera and " +
              "restarting OBS both do nothing here, because neither makes Windows re-enumerate the card."
            : " If the camera really is on, unplug and re-plug the card's USB cable, or reboot: restarting " +
              "the camera and restarting OBS both do nothing here, because neither makes Windows " +
              "re-enumerate the card.";

        return new PreflightItem
        {
            Key = "video",
            Ok = false,
            Message = new Msg(zh, en),
            Action = canReset ? "reset-capture" : null,
        };
    }

    private static PreflightItem Ok(string key, Msg message) => new() { Key = key, Ok = true, Message = message };

    private static PreflightItem Fail(string key, Msg message) => new() { Key = key, Ok = false, Message = message };
}
