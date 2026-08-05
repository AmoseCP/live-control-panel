using LiveControlPanel.Config;

namespace LiveControlPanel.Api;

/// <summary>
/// Access-code gate (FR 6.4). Deliberately not a login: the URL in the iPad home-screen icon carries
/// <c>?k={accessCode}</c>, whoever scanned the QR is in, and nobody types a password at 04:40.
///
/// Static files stay open so the page can load and stash the code; the API and the WebSocket require
/// it. Settings endpoints additionally require the PIN, which guards against mis-edits by seven
/// people sharing one PC — not against an attacker.
/// </summary>
public sealed class AccessGate
{
    public const string CodeHeader = "X-Access-Code";
    public const string PinHeader = "X-Settings-Pin";
    public const string CodeQuery = "k";
    public const string CodeCookie = "lcp_k";

    private readonly ConfigStore _config;

    public AccessGate(ConfigStore config) => _config = config;

    public bool IsValidCode(HttpContext context) => Matches(ExtractCode(context), _config.Settings.AccessCode);

    public bool IsValidPin(HttpContext context)
    {
        var pin = context.Request.Headers[PinHeader].FirstOrDefault();
        return Matches(pin, _config.Settings.SettingsPin);
    }

    private static string? ExtractCode(HttpContext context)
    {
        if (context.Request.Query.TryGetValue(CodeQuery, out var query) && !string.IsNullOrEmpty(query))
            return query.ToString();

        var header = context.Request.Headers[CodeHeader].FirstOrDefault();
        if (!string.IsNullOrEmpty(header)) return header;

        return context.Request.Cookies.TryGetValue(CodeCookie, out var cookie) ? cookie : null;
    }

    /// <summary>Fixed-time comparison. Cheap, and stops a bored teenager on the church WiFi.</summary>
    private static bool Matches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        if (string.IsNullOrEmpty(provided)) return false;

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
