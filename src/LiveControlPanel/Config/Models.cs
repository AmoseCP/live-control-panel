using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveControlPanel.Config;

/// <summary>A recurring meeting. FR 3.1.</summary>
public sealed class ServiceTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TitleFormat { get; set; } = "{M}/{D}/{YYYY} {name}";

    /// <summary>0 = Sunday .. 6 = Saturday. Empty means "never matches automatically".</summary>
    public List<int> Weekdays { get; set; } = new();

    /// <summary>Local time "HH:mm". Null/empty means the template is manual-only (the built-in "custom").</summary>
    public string? StartTime { get; set; }

    public string? Description { get; set; }
    public string? ThumbnailFile { get; set; }
    public string? TelegramMessage { get; set; }
    public string PrivacyStatus { get; set; } = "unlisted";
    public bool MadeForKids { get; set; }
    public string LatencyPreference { get; set; } = "ultraLow";

    public ServiceTemplate Clone() => (ServiceTemplate)MemberwiseClone();
}

public sealed class ObsSettings
{
    public string Url { get; set; } = "ws://localhost:4455";
    public string Password { get; set; } = "";
    public string SceneCamera { get; set; } = "摄像机";
    public string SceneSlides { get; set; } = "PPT";

    /// <summary>Audio input watched by the pre-flight level check (FR 4.4 "audio").</summary>
    public string AudioInputName { get; set; } = "ProFX";

    /// <summary>
    /// Sources checked by the pre-flight "video" item (FR 4.4). Not in the original settings
    /// sketch, but the check cannot be performed without knowing which sources to look at.
    /// </summary>
    public List<string> VideoSourceNames { get; set; } = new();
}

public sealed class SlidesSettings
{
    /// <summary>Determined at deploy time via /api/diag/windows. Never hard-coded (FR 5.3).</summary>
    public string WindowClass { get; set; } = "";

    public string WindowTitleRegex { get; set; } = "";

    /// <summary>"PostMessage" (default, does not steal focus) or "SendInput" (fallback).</summary>
    public string Strategy { get; set; } = "PostMessage";
}

/// <summary>
/// How far from a service's nominal start time the panel still recognises it (FR 4.1).
///
/// The start times in the templates are the announced times, not what actually happens — an
/// operator may arrive early or run late. The window is wider on the "after" side because running
/// late is both more common and more stressful: a late operator must not be told "本日无排期".
///
/// -60/+120 stays unambiguous with the shipped schedule: Wednesday and Friday carry both an 04:40
/// and an 18:00 service, and 04:40+120 = 06:40 never reaches 18:00-60 = 17:00.
/// </summary>
public sealed class MatchWindowSettings
{
    public int BeforeMinutes { get; set; } = 60;
    public int AfterMinutes { get; set; } = 120;
}

/// <summary>
/// OAuth client credentials. FR 5.1 requires a Desktop-app OAuth client but the settings sketch
/// in FR 3.2 has nowhere to put it, so it lives here and is editable from the settings page.
/// </summary>
public sealed class YouTubeSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Days of validity assumed for a refresh token, used for the "authorization expires in N days"
    /// warning (FR 8). Google does not publish an expiry for tokens of published apps; six months
    /// mirrors the documented inactivity limit and keeps the countdown conservative.
    /// </summary>
    public int AssumedValidityDays { get; set; } = 180;
}

public sealed class AppSettings
{
    public int Port { get; set; } = 5088;
    public string AccessCode { get; set; } = "";
    public string SettingsPin { get; set; } = "";
    public string StreamId { get; set; } = "";
    public string DefaultDescription { get; set; } = "God Bless You!";
    public string DefaultThumbnail { get; set; } = "thumbnails/default.jpg";
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public string TelegramMessageDefault { get; set; } = "{title}\n{url}";
    public ObsSettings Obs { get; set; } = new();
    public SlidesSettings Slides { get; set; } = new();
    public MatchWindowSettings MatchWindow { get; set; } = new();
    public YouTubeSettings YouTube { get; set; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
