using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LiveControlPanel.Config;
using QRCoder;

namespace LiveControlPanel.Net;

public sealed record AccessAddress(string Url, string Address, string AdapterName);

public sealed record AccessInfo(
    IReadOnlyList<AccessAddress> Addresses,
    string? MdnsUrl,
    string LocalUrl,
    string AccessCode,
    string QrPngBase64);

/// <summary>
/// LAN access addresses and a QR code (FR 6.4). Addresses are discovered at runtime — never
/// hard-coded — because the church PC's IP changes and the panel must still be reachable.
/// </summary>
public sealed class AccessInfoProvider
{
    /// <summary>
    /// Adapter name/description fragments that never carry church WiFi traffic. Listing these would
    /// send an operator to an address that cannot work.
    /// </summary>
    private static readonly string[] VirtualAdapterMarkers =
    {
        "hyper-v", "vethernet", "virtualbox", "vmware", "vmnet", "loopback", "wsl",
        "docker", "tap-", "tap adapter", "openvpn", "wireguard", "tailscale", "zerotier",
        "npcap", "bluetooth", "pseudo-interface", "teredo", "isatap",
    };

    private readonly ConfigStore _config;

    public AccessInfoProvider(ConfigStore config) => _config = config;

    public AccessInfo Get()
    {
        var port = _config.Settings.Port;
        var code = _config.Settings.AccessCode;

        var addresses = UsableAddresses()
            .Select(a => new AccessAddress($"http://{a.Address}:{port}/?k={code}", a.Address, a.AdapterName))
            .ToList();

        var mdnsHost = MdnsHostName();
        var mdnsUrl = mdnsHost is null ? null : $"http://{mdnsHost}:{port}/?k={code}";

        // The QR encodes the first real LAN address; falling back to mDNS then localhost keeps the
        // endpoint answering even on a machine with no active adapter.
        var qrTarget = addresses.FirstOrDefault()?.Url ?? mdnsUrl ?? $"http://localhost:{port}/?k={code}";

        return new AccessInfo(
            addresses,
            mdnsUrl,
            $"http://localhost:{port}/?k={code}",
            code,
            QrPngBase64(qrTarget));
    }

    /// <summary>IPv4 addresses on up, non-virtual, non-loopback adapters.</summary>
    internal static List<(string Address, string AdapterName)> UsableAddresses()
    {
        var results = new List<(string, string)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (IsVirtual(nic.Name) || IsVirtual(nic.Description)) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;

                var text = unicast.Address.ToString();
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue; // link-local, unroutable

                results.Add((text, nic.Name));
            }
        }

        return results;
    }

    internal static bool IsVirtual(string? nameOrDescription)
    {
        if (string.IsNullOrWhiteSpace(nameOrDescription)) return false;
        var value = nameOrDescription.ToLowerInvariant();
        return VirtualAdapterMarkers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    private static string? MdnsHostName()
    {
        var host = Environment.MachineName;
        return string.IsNullOrWhiteSpace(host) ? null : $"{host.ToLowerInvariant()}.local";
    }

    private static string QrPngBase64(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return Convert.ToBase64String(png);
    }
}
