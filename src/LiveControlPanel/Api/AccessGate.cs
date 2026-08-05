using LiveControlPanel.Config;
using LiveControlPanel.Core;

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

public static class AccessGateMiddleware
{
    /// <summary>
    /// Installs the gate. Lives here rather than in Program.cs so the tests exercise the real
    /// middleware — a copy of these rules in the test host is how a 403 body drifted out of sync with
    /// production once already.
    /// </summary>
    public static void UseAccessGate(this WebApplication app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;

        // Static files stay open so the page can load and stash the code. /auth/callback is open
        // because Google's redirect cannot carry it.
        var guarded = path.StartsWithSegments("/api")
                      || path.StartsWithSegments("/ws")
                      || path.StartsWithSegments("/auth/start");

        if (guarded)
        {
            var gate = context.RequestServices.GetRequiredService<AccessGate>();
            if (!gate.IsValidCode(context))
            {
                // Reason tags which gate refused. The PIN gate also answers 403, and the page must
                // not tell someone their PIN is wrong when the real problem is a stale access code
                // cached from a previous install.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new GateResult(
                    false,
                    new Msg(
                        "访问码无效。请重新扫描二维码，或用管理员给的完整链接打开。",
                        "Invalid access code. Re-scan the QR code, or open the full link the administrator gave you."),
                    GateResult.BadAccessCode), Json.Options);
                return;
            }

            // Persist the code so a page opened from the QR keeps working after in-page navigation
            // drops the query string.
            if (context.Request.Query.ContainsKey(AccessGate.CodeQuery))
            {
                context.Response.Cookies.Append(
                    AccessGate.CodeCookie,
                    context.Request.Query[AccessGate.CodeQuery].ToString(),
                    new CookieOptions
                    {
                        HttpOnly = false,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                    });
            }
        }

        await next();
    });
}
