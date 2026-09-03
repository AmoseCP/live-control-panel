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

    /// <summary>
    /// Whether this service also gets the AI-translated second broadcast. Defaults to true so that
    /// switching translation on in settings covers every service; an individual service that should
    /// stay single-language turns it off here. Has no effect while
    /// <see cref="TranslationSettings.Enabled"/> is false.
    /// </summary>
    public bool Translate { get; set; } = true;

    /// <summary>
    /// BCP-47 target language for this service, overriding <see cref="TranslationSettings.TargetLanguage"/>.
    /// Null follows the global setting. This is what makes a mixed schedule work: a Chinese service
    /// carries "en" and an English service carries "zh-CN", and the model detects the source itself.
    /// </summary>
    public string? TargetLanguage { get; set; }

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
    /// <summary>
    /// Off until switched on. Slide control is the one feature that reaches outside the panel into
    /// another application — synthesizing keystrokes at a window and attaching to a COM automation
    /// object. Until someone has confirmed on the actual machine which of those work (see
    /// /api/diag/com-probe), the safe default is to not touch the presentation program at all and to
    /// keep the paging controls off the operator's screen.
    /// </summary>
    public bool Enabled { get; set; }

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

/// <summary>
/// The AI-translated second broadcast.
///
/// One camera feed, two YouTube broadcasts: OBS encodes the video once and fans it out to two RTMP
/// targets (obs-multi-rtmp with the video encoder set to "same as OBS output"), each carrying a
/// different audio track. Track 1 is the mixer; track 2 is the voice this panel synthesizes by
/// streaming the mixer audio through Gemini's live translation model and playing the result into a
/// virtual audio cable that OBS captures.
///
/// Nothing here may ever be able to disturb the primary broadcast. The panel starts one OBS stream
/// exactly as before — the plugin's "sync start/stop with OBS" carries the second target — so a
/// dead translator produces a silent English stream and a warning, never an interrupted service.
/// </summary>
public sealed class TranslationSettings
{
    /// <summary>
    /// Off until deliberately switched on. Everything downstream — the second broadcast, the second
    /// stream key, the Gemini session, the audio devices — is gated on this, so a panel that has
    /// never been configured for translation behaves exactly as it did before.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Google AI Studio API key for the Gemini Live API.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// The live translation model. Pinned in settings rather than in code because preview model ids
    /// are renamed on Google's schedule, not ours, and a rename must be fixable without a rebuild on
    /// a church PC.
    /// </summary>
    public string Model { get; set; } = "models/gemini-3.5-live-translate-preview";

    /// <summary>BCP-47 code of the language the second broadcast speaks. A service may override it.</summary>
    public string TargetLanguage { get; set; } = "en";

    /// <summary>
    /// Passed to the model as translationConfig.echoTargetLanguage. True means "when the speaker is
    /// already speaking the target language, pass it through instead of falling silent" — which is
    /// the whole reason a single session survives a bilingual speaker who switches mid-sentence.
    /// </summary>
    public bool EchoTargetLanguage { get; set; } = true;

    /// <summary>Appended to the primary title to name the second broadcast.</summary>
    public string TitleSuffix { get; set; } = " (English)";

    /// <summary>
    /// The second reusable YouTube stream key, created once on the settings page and entered into
    /// the obs-multi-rtmp target. Separate from <see cref="AppSettings.StreamId"/> because one
    /// liveStream can only be bound to one live broadcast at a time.
    /// </summary>
    public string StreamId { get; set; } = "";

    /// <summary>
    /// WASAPI device id the original speech is read from — the mixer's own USB interface, the same
    /// device OBS captures. Shared mode, so both can hold it open. Empty means the system default
    /// recording device, which on this PC is usually the wrong one.
    /// </summary>
    public string CaptureDeviceId { get; set; } = "";

    /// <summary>
    /// WASAPI device the translated voice is played to — the virtual cable's input (VB-CABLE's
    /// "CABLE Input"). It must NOT be the mixer's playback side: that would put the translation into
    /// the house PA and back into the main mix.
    /// </summary>
    public string PlaybackDeviceId { get; set; } = "";

    /// <summary>
    /// Name of the OBS input that captures the cable's output, checked by the pre-flight the same way
    /// the mixer input is. Empty skips that check.
    /// </summary>
    public string ObsInputName { get; set; } = "";

    /// <summary>
    /// Which OBS audio track the translated voice is routed to. Recorded for the deployment
    /// checklist and the settings page only — OBS's track routing is not reachable over
    /// obs-websocket, so this is documentation, not control.
    /// </summary>
    public int ObsAudioTrack { get; set; } = 2;
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
    public TranslationSettings Translation { get; set; } = new();
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
