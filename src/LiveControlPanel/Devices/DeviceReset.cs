using LiveControlPanel.Core;

namespace LiveControlPanel.Devices;

/// <summary>One Plug and Play device, as offered on the settings page.</summary>
public sealed record PnpDeviceInfo(string InstanceId, string Name, string Class, string Status)
{
    /// <summary>Windows reports "OK" for a working device; anything else is worth showing.</summary>
    public bool Healthy => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);
}

public sealed record DeviceResetResult(bool Ok, Msg Message);

/// <summary>
/// Disables and re-enables a USB device so Windows re-enumerates it.
///
/// This exists for one observed failure: the USB capture card wedges, and neither restarting OBS nor
/// restarting the camera clears it — because neither re-enumerates the device. Only a reboot or
/// re-plugging the card does. At 04:40, alone, both of those mean crawling behind a PC or losing the
/// machine for two minutes; this is the same recovery from the panel.
/// </summary>
public interface IDeviceResetter
{
    /// <summary>Devices plausibly worth resetting — the full PnP list is hundreds of rows.</summary>
    Task<IReadOnlyList<PnpDeviceInfo>> ListCandidatesAsync(CancellationToken ct = default);

    Task<PnpDeviceInfo?> FindAsync(string instanceId, CancellationToken ct = default);

    Task DisableAsync(string instanceId, CancellationToken ct = default);

    Task EnableAsync(string instanceId, CancellationToken ct = default);
}

/// <summary>Windows refused the disable/enable — almost always because the panel is not elevated.</summary>
public sealed class DeviceResetFailedException : Exception
{
    public DeviceResetFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// What may and may not be reset.
///
/// Pointing this at the wrong device is the whole risk of the feature: a reset aimed at the mixer's
/// USB audio silences the service, and one aimed at a root hub takes the keyboard with it. Three
/// things contain that, and all three matter:
///
/// <list type="number">
/// <item>The reset endpoint takes <b>no device parameter</b>. It can only touch what an administrator
/// already chose behind the PIN, so a stray request cannot name a device of its own.</item>
/// <item>Device classes that would be catastrophic are refused outright, even if configured.</item>
/// <item>It is off until deliberately switched on, like slide control.</item>
/// </list>
/// </summary>
public static class DeviceResetGuard
{
    /// <summary>
    /// Classes never eligible. Hubs are here because resetting a hub takes everything downstream of
    /// it — including, on most of these machines, the keyboard and the mixer.
    /// </summary>
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB",           // hubs and host controllers
        "HIDClass",
        "Keyboard",
        "Mouse",
        "DiskDrive",
        "Volume",
        "Net",
        "System",
        "Computer",
        "Processor",
        "Display",
        "SCSIAdapter",
    };

    /// <summary>
    /// Classes offered in the settings-page picker. A capture card lands in one of these depending
    /// on whether it uses the class driver or the vendor's own.
    /// </summary>
    private static readonly HashSet<string> Candidates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Camera",
        "Image",
        "Media",
        "AudioEndpoint",
        "MEDIA",
        "USBDevice",
    };

    public static bool IsCandidate(PnpDeviceInfo device) =>
        !Refused.Contains(device.Class)
        && (Candidates.Contains(device.Class) || LooksLikeCaptureHardware(device.Name));

    /// <summary>Null when the device may be reset; otherwise why not, in the operator's words.</summary>
    public static Msg? Refuse(PnpDeviceInfo device)
    {
        if (!Refused.Contains(device.Class)) return null;

        return new Msg(
            $"「{device.Name}」属于 {device.Class} 类设备，面板不会重置它 —— " +
            "重置 USB 集线器或输入设备会把键盘、调音台一起带下去。请在设置页改选采集卡本身。",
            $"\"{device.Name}\" is a {device.Class} device and the panel will not reset it: resetting a " +
            "USB hub or an input device takes the keyboard and the mixer down with it. Pick the capture " +
            "card itself on the settings page.");
    }

    /// <summary>
    /// A vendor driver can register under a class name of its own invention, so the picker also
    /// matches on the names capture hardware actually ships with.
    /// </summary>
    private static bool LooksLikeCaptureHardware(string name)
    {
        foreach (var hint in new[] { "AVer", "Capture", "采集", "Elgato", "Magewell", "HDMI", "UVC", "Video" })
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
