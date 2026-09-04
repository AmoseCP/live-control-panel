using System.Management;
using System.Runtime.Versioning;

namespace LiveControlPanel.Devices;

/// <summary>
/// Disable/enable through WMI's <c>Win32_PnPEntity</c>.
///
/// WMI rather than shelling out to <c>powershell.exe -Command Disable-PnpDevice</c>: spawning a shell
/// from the panel costs a second of process startup, gives back a string instead of a status code,
/// and puts the recovery path behind whatever execution policy the machine happens to carry. WMI is
/// also what <c>Disable-PnpDevice</c> itself calls.
///
/// Both methods need an elevated caller. The panel's scheduled task is registered with
/// <c>-RunLevel Highest</c> for exactly this reason, and a non-elevated panel is told so by name
/// rather than being handed an access-denied code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiDeviceResetter : IDeviceResetter
{
    private readonly ILogger<WmiDeviceResetter> _log;

    public WmiDeviceResetter(ILogger<WmiDeviceResetter> log) => _log = log;

    public Task<IReadOnlyList<PnpDeviceInfo>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PnpDeviceInfo>>(() =>
        {
            var devices = new List<PnpDeviceInfo>();

            try
            {
                // Present devices only: the PnP store remembers every USB device ever plugged into
                // this machine, and offering a picker full of absent hardware is how the wrong
                // device gets chosen.
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, PNPClass, Status FROM Win32_PnPEntity WHERE Present = TRUE");

                // The collection is disposable too. Leaving it to the finalizer holds a WMI
                // enumerator and its COM objects on a process that runs for weeks.
                using var results = searcher.Get();

                foreach (var item in results)
                {
                    using var device = (ManagementObject)item;
                    var info = Read(device);
                    if (info is not null && DeviceResetGuard.IsCandidate(info)) devices.Add(info);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Enumerating PnP devices failed");
            }

            return devices
                .OrderBy(d => d.Class, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    public Task<PnpDeviceInfo?> FindAsync(string instanceId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                using var device = Open(instanceId);
                return device is null ? null : Read(device);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Looking up PnP device {InstanceId} failed", instanceId);
                return null;
            }
        }, ct);

    public Task DisableAsync(string instanceId, CancellationToken ct = default) =>
        Task.Run(() => Invoke(instanceId, "Disable"), ct);

    public Task EnableAsync(string instanceId, CancellationToken ct = default) =>
        Task.Run(() => Invoke(instanceId, "Enable"), ct);

    private void Invoke(string instanceId, string method)
    {
        using var device = Open(instanceId)
            ?? throw new DeviceResetFailedException($"Device {instanceId} was not found.");

        object? raw;
        try
        {
            raw = device.InvokeMethod(method, Array.Empty<object>());
        }
        catch (ManagementException ex)
        {
            throw new DeviceResetFailedException(
                $"{method} was refused by Windows ({ex.ErrorCode}).", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DeviceResetFailedException($"{method} needs an elevated process.", ex);
        }

        // Win32_PnPEntity.Disable/Enable answer with a status code rather than throwing. Zero is
        // success; everything else has to surface, or a refused reset would look like a done one.
        var status = raw is null ? 0u : Convert.ToUInt32(raw);
        if (status != 0)
            throw new DeviceResetFailedException($"{method} returned status {status}.");

        _log.LogInformation("{Method} succeeded for device {InstanceId}", method, instanceId);
    }

    private static ManagementObject? Open(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;

        // WQL escapes with a backslash, not by doubling the quote — doubling is the SQL convention,
        // and using it here produced a malformed query that came back as "the configured device is
        // not present on this PC". Device ids do not contain quotes in practice, but the comment
        // claimed the escaping was right, which is the part worth fixing.
        var escaped = instanceId.Replace("\\", "\\\\").Replace("'", "\\'");

        using var searcher = new ManagementObjectSearcher(
            $"SELECT DeviceID, Name, PNPClass, Status FROM Win32_PnPEntity WHERE DeviceID = '{escaped}'");

        // Enumerated to completion so the collection and every object in it are released. Returning
        // from inside the loop abandoned the enumerator mid-iteration.
        using var results = searcher.Get();

        ManagementObject? found = null;
        foreach (var item in results)
        {
            var device = (ManagementObject)item;
            if (found is null) found = device;
            else device.Dispose();
        }

        return found;
    }

    private static PnpDeviceInfo? Read(ManagementObject device)
    {
        var id = device["DeviceID"] as string;
        if (string.IsNullOrWhiteSpace(id)) return null;

        return new PnpDeviceInfo(
            id,
            device["Name"] as string ?? id,
            device["PNPClass"] as string ?? "",
            device["Status"] as string ?? "");
    }
}
