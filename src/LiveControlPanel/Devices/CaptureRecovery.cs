using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Obs;

namespace LiveControlPanel.Devices;

/// <summary>
/// The one-tap recovery for a wedged USB capture card: disable the device, wait, enable it, then
/// make OBS re-open it.
///
/// Why this exists at all is worth stating, because from the panel's usual position — "it is only a
/// remote control" — reaching into Device Manager looks out of character. The observed failure is
/// that the capture card stops producing a picture and <b>neither restarting OBS nor restarting the
/// camera fixes it</b>, because neither re-enumerates a USB device. What fixes it is a reboot or
/// re-plugging the card. At 04:40, alone, both mean either losing the machine for two minutes or
/// crawling behind it in the dark. This is the same recovery, from the iPad.
/// </summary>
public sealed class CaptureRecovery
{
    /// <summary>
    /// How long the device is left disabled — long enough for Windows to actually tear it down.
    ///
    /// The three delays here are settable for the same reason the orchestrator's live-poll timeout
    /// is: a suite that waits out five real seconds per case stops being run.
    /// </summary>
    internal TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the device is given to come back before OBS is asked to re-open it.</summary>
    internal TimeSpan ReadyDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gap between attempts to switch the device back on.</summary>
    internal TimeSpan EnableRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A failed enable leaves the device switched off, which is worse than the fault being recovered
    /// from — so it is retried rather than reported on the first refusal.
    /// </summary>
    private const int EnableAttempts = 3;

    private readonly ConfigStore _config;
    private readonly StateManager _state;
    private readonly IDeviceResetter _devices;
    private readonly IObsClient _obs;
    private readonly ILogger<CaptureRecovery> _log;

    /// <summary>
    /// Taken with a zero timeout, so a second tap during a reset is refused rather than queued.
    /// Queuing would disable a device that a previous run is about to re-enable.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CaptureRecovery(
        ConfigStore config,
        StateManager state,
        IDeviceResetter devices,
        IObsClient obs,
        ILogger<CaptureRecovery> log)
    {
        _config = config;
        _state = state;
        _devices = devices;
        _obs = obs;
        _log = log;
    }

    public async Task<DeviceResetResult> ResetAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new DeviceResetResult(true, new Msg(
                "正在重置采集卡，请稍候…", "Resetting the capture card, please wait…"));

        try
        {
            return await ResetCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeviceResetResult> ResetCoreAsync(CancellationToken ct)
    {
        var settings = _config.Settings.CaptureReset;

        if (!settings.Enabled)
            return Fail(
                "重置采集卡这个功能还没有启用。请让管理员在设置页打开并选好设备。",
                "Resetting the capture card is not switched on. Ask the administrator to enable it and " +
                "pick the device on the settings page.");

        if (string.IsNullOrWhiteSpace(settings.DeviceInstanceId))
            return Fail(
                "还没有选好要重置哪个设备。请让管理员在设置页选中采集卡。",
                "No device has been chosen yet. Ask the administrator to select the capture card on the " +
                "settings page.");

        var device = await _devices.FindAsync(settings.DeviceInstanceId, ct).ConfigureAwait(false);
        if (device is null)
            return Fail(
                $"在这台电脑上找不到已配置的设备（{Describe(settings)}）。" +
                "采集卡换过 USB 口时它的设备编号会变 —— 请让管理员在设置页重新选一次。",
                $"The configured device ({Describe(settings)}) is not present on this PC. A capture card " +
                "moved to another USB port comes back with a different device id — ask the administrator " +
                "to pick it again on the settings page.");

        // The guard runs here, not only when the device was chosen: settings files get hand-edited,
        // and a reset aimed at a hub would take the keyboard and the mixer down with it.
        if (DeviceResetGuard.Refuse(device) is { } refusal)
        {
            _log.LogWarning("Refused to reset {Name} of class {Class}", device.Name, device.Class);
            return new DeviceResetResult(false, refusal);
        }

        _log.LogInformation("Resetting capture device {Name} ({InstanceId})", device.Name, device.InstanceId);

        try
        {
            await _devices.DisableAsync(device.InstanceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Disabling {Name} failed", device.Name);
            return await DescribeFailedDisableAsync(device, ct).ConfigureAwait(false);
        }

        try { await Task.Delay(SettleDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* still must re-enable below */ }

        // The worst outcome this method has: the card is off and stayed off.
        if (!await TryEnableAsync(device, ct).ConfigureAwait(false)) return LeftDisabled(device);

        _state.Mutate(s => s.CaptureReset.LastResetAt = DateTime.Now);
        _state.RecordAction(new Msg("重置采集卡", "Reset the capture card"));

        try { await Task.Delay(ReadyDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* the reset already succeeded */ }

        // Best effort from here. The device is back either way, and the remaining step is a
        // convenience — one an operator can do by hand in OBS if it does not take.
        var refreshed = await TryRefreshObsAsync(settings.ObsInputName, ct).ConfigureAwait(false);

        return new DeviceResetResult(true, refreshed
            ? new Msg(
                "采集卡已重置，OBS 已重新打开画面来源。请看一眼 OBS 预览确认画面回来了。",
                "The capture card was reset and OBS has re-opened the source. Glance at the OBS preview to " +
                "confirm the picture is back.")
            : new Msg(
                "采集卡已重置。请看一眼 OBS 预览；如果画面还没回来，在 OBS 里点那个画面来源旁边的眼睛" +
                "图标关掉再打开。",
                "The capture card was reset. Glance at the OBS preview; if the picture has not come back, " +
                "click the eye icon next to that video source in OBS off and on again."));
    }

    /// <summary>
    /// A disable that threw did not necessarily fail to disable.
    ///
    /// <see cref="WmiDeviceResetter"/> raises its exception <b>after</b> the WMI method has run,
    /// whenever that method answers a non-zero status — and some of those statuses mean the device
    /// really was switched off. Reporting "could not switch the card off" there would tell the
    /// operator the exact opposite of what happened, on the one failure that leaves the card dead.
    ///
    /// So the device's actual state picks the message, and if it looks off, it is put back first.
    /// </summary>
    private async Task<DeviceResetResult> DescribeFailedDisableAsync(
        PnpDeviceInfo device, CancellationToken ct)
    {
        PnpDeviceInfo? current = null;
        try
        {
            current = await _devices.FindAsync(device.InstanceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Re-reading {Name} after a failed disable failed", device.Name);
        }

        // Present and healthy: the disable genuinely did not take, so the card is untouched and the
        // usual cause is that the panel is not elevated.
        if (current is { Healthy: true }) return CannotDisable();

        // Anything else — disabled, errored, or gone from the list — means it may be switched off.
        // Put it back before saying anything.
        if (await TryEnableAsync(device, ct).ConfigureAwait(false))
        {
            return Fail(
                "重置没有成功，但采集卡现在是开着的。请拔插采集卡的 USB 线，或重启电脑。" +
                "若反复如此，请让管理员确认面板是以管理员身份运行的（部署文档 6.1 节的 -RunLevel Highest）。",
                "The reset did not go through, but the capture card is switched on. Unplug and re-plug its " +
                "USB cable, or reboot. If this keeps happening, ask the administrator to confirm the panel " +
                "runs elevated (-RunLevel Highest, section 6.1 of the deployment guide).");
        }

        return LeftDisabled(device);
    }

    private static DeviceResetResult CannotDisable() => Fail(
        "无法关闭采集卡。面板需要以管理员身份运行才能做这件事 —— " +
        "请让管理员按部署文档第 6.1 节用带 -RunLevel Highest 的脚本重建计划任务。" +
        "眼下的替代办法：拔插采集卡的 USB 线，或重启电脑。",
        "Could not switch the capture card off. The panel has to run elevated for this — ask the " +
        "administrator to re-register the scheduled task with -RunLevel Highest as in section 6.1 " +
        "of the deployment guide. For now: unplug and re-plug the card's USB cable, or reboot.");

    /// <summary>Said only when the device has actually been confirmed to still be off.</summary>
    private static DeviceResetResult LeftDisabled(PnpDeviceInfo device) => Fail(
        $"采集卡已关闭，但重新打开失败，现在它是禁用状态。请在「设备管理器」里找到" +
        $"「{device.Name}」，右键 → 启用设备；若仍不行请重启电脑。",
        $"The capture card was switched off but could not be switched back on, so it is currently " +
        $"disabled. Open Device Manager, find \"{device.Name}\", right-click and choose Enable " +
        "device; if that does not work, reboot the PC.");

    private async Task<bool> TryEnableAsync(PnpDeviceInfo device, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= EnableAttempts; attempt++)
        {
            try
            {
                await _devices.EnableAsync(device.InstanceId, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Enabling {Name} failed on attempt {Attempt}", device.Name, attempt);

                if (attempt == EnableAttempts) return false;

                // Not linked to ct: a cancelled request must not be the reason a device stays off.
                try { await Task.Delay(EnableRetryDelay, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* nothing left to wait on */ }
            }
        }

        return false;
    }

    private async Task<bool> TryRefreshObsAsync(string inputName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputName)) return false;
        if (!_obs.Status.Connected) return false;

        try
        {
            await _obs.RefreshInputAsync(inputName, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Re-applying settings for OBS input {Input} failed", inputName);
            return false;
        }
    }

    private static string Describe(CaptureResetSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DeviceName) ? settings.DeviceInstanceId : settings.DeviceName;

    private static DeviceResetResult Fail(string zh, string en) =>
        new(false, new Msg(zh, en));
}
