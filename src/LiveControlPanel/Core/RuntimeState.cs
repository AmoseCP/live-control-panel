namespace LiveControlPanel.Core;

public static class Phase
{
    public const string NoSchedule = "NoSchedule";
    public const string Ready = "Ready";
    public const string Live = "Live";
    public const string Ended = "Ended";
}

public static class BroadcastStatus
{
    public const string Created = "created";
    public const string Bound = "bound";
    public const string Testing = "testing";
    public const string Live = "live";
    public const string Complete = "complete";
}

public sealed class TodayState
{
    public string? TemplateId { get; set; }
    public string? Title { get; set; }
    public DateTime? ScheduledStart { get; set; }

    /// <summary>Set only by manual creation; overrides the template/default description for this run.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True when the operator chose this service explicitly ("不是这一场？" or an ad-hoc title).
    /// Automatic schedule matching must never overwrite or clear a manual choice.
    /// </summary>
    public bool Manual { get; set; }
}

public sealed class NextServiceState
{
    public string? Title { get; set; }
    public DateTime? StartsAt { get; set; }

    /// <summary>
    /// Which template it is, so an operator who arrived early can start preparing that service in one
    /// tap instead of going through the picker.
    /// </summary>
    public string? TemplateId { get; set; }
}

public sealed class BroadcastState
{
    public string? Id { get; set; }
    public string? WatchUrl { get; set; }
    public string Status { get; set; } = BroadcastStatus.Created;
    public string? Title { get; set; }

    /// <summary>Orchestration bookkeeping — the basis for idempotency (FR 4.2).</summary>
    public bool ThumbnailUploaded { get; set; }

    /// <summary>
    /// When the panel created this broadcast, by the panel's clock. The day rollover retires
    /// yesterday's work by this date — Today cannot serve that purpose because the auto-matched
    /// Today empties itself as soon as the match window lapses, hours before midnight.
    /// </summary>
    public DateTime CreatedOn { get; set; }
}

public sealed class ObsState
{
    public bool Connected { get; set; }
    public bool Streaming { get; set; }
    public long StreamTimeSeconds { get; set; }
    public string? CurrentScene { get; set; }
    public double DroppedFramesPercent { get; set; }
    public long KbitsPerSec { get; set; }
    public List<string> Scenes { get; set; } = new();

    /// <summary>
    /// OBS has lost YouTube's ingest and is retrying. The stream timer keeps running and the bitrate
    /// still reads healthy while this is true, so without surfacing it the panel shows a perfectly
    /// normal live card during the one failure the operator most needs to know about.
    /// </summary>
    public bool Reconnecting { get; set; }

    /// <summary>0..1, OBS's own measure of how hard it is finding it to push frames out.</summary>
    public double Congestion { get; set; }
}

public sealed class SlidesState
{
    /// <summary>Whether slide control is switched on at all (settings.slides.enabled).</summary>
    public bool Enabled { get; set; }

    public bool Available { get; set; }
    public int? Current { get; set; }
    public int? Total { get; set; }
}

/// <summary>
/// Whether the panel can re-enumerate the capture card, and when it last did.
///
/// The operator page keys the reset button on this, so a panel that never configured it shows
/// nothing — and one that did shows the last attempt, because "did I already try that" is the first
/// question at 04:40.
/// </summary>
public sealed class CaptureResetState
{
    public bool Enabled { get; set; }
    public string? DeviceName { get; set; }
    public DateTime? LastResetAt { get; set; }
}

/// <summary>
/// The AI translator's live health, pushed to the operator page so a silent second broadcast is
/// visible while it can still be fixed — never blocking, never touching the primary stream.
/// </summary>
public sealed class TranslationState
{
    /// <summary>Translation is switched on in settings AND this service opts into it.</summary>
    public bool Enabled { get; set; }

    /// <summary>The audio devices are open and a session has been asked for.</summary>
    public bool Running { get; set; }

    /// <summary>The Gemini session is up and has acknowledged setup.</summary>
    public bool Connected { get; set; }

    /// <summary>BCP-47 code the second broadcast is speaking, for this service.</summary>
    public string? TargetLanguage { get; set; }

    /// <summary>
    /// When translated audio was last written to the virtual cable. This — not "connected" — is what
    /// says the English stream has sound: a session can be up and producing nothing.
    /// </summary>
    public DateTime? LastAudioAt { get; set; }

    /// <summary>Peak (0..1) of what is being sent to the model, so a dead mixer feed is visible.</summary>
    public double InputPeak { get; set; }

    /// <summary>Peak (0..1) of what is being played into the cable.</summary>
    public double OutputPeak { get; set; }

    /// <summary>Most recent translated line, purely so a human can confirm it is translating sense.</summary>
    public string? LastTranscript { get; set; }

    public Msg? LastError { get; set; }
}

public sealed class TelegramState
{
    public DateTime? SentAt { get; set; }
    public Msg? LastError { get; set; }
}

public sealed class PreflightItem
{
    public string Key { get; set; } = "";
    public bool Ok { get; set; }
    public Msg Message { get; set; } = Msg.Empty;

    /// <summary>An action the operator can take from the panel, e.g. "end-previous".</summary>
    public string? Action { get; set; }
}

public sealed class AuthState
{
    public bool Valid { get; set; }
    public int? ExpiresInDays { get; set; }
    public DateTime? AuthorizedAt { get; set; }
}

/// <summary>One step of the start-today orchestration (FR 4.2), surfaced so the UI can show progress.</summary>
public sealed class StepState
{
    public int Step { get; set; }
    public Msg Name { get; set; } = Msg.Empty;

    /// <summary>
    /// pending | running | done | skipped | warn | failed.
    ///
    /// "warn" exists for the translation step alone: it did not do what it was asked, but the
    /// service must still go on air. Unlike "failed" it does not stop the sequence and does not
    /// offer a retry — the operator fixes it from the live card, or lets the English stream run
    /// silent.
    /// </summary>
    public string Status { get; set; } = "pending";

    public Msg? Message { get; set; }
}

public sealed class LastActionState
{
    public DateTime At { get; set; }
    public Msg What { get; set; } = Msg.Empty;
    public string? Service { get; set; }
}

/// <summary>FR 3.4. Held in memory only and pushed over the WebSocket; never persisted.</summary>
public sealed class RuntimeState
{
    public string Phase { get; set; } = Core.Phase.NoSchedule;
    public TodayState? Today { get; set; }
    public NextServiceState? NextService { get; set; }
    public BroadcastState? Broadcast { get; set; }

    /// <summary>
    /// The second, AI-translated broadcast. Null whenever translation is off for this service — the
    /// UI keys every translation affordance on this being present, so an unconfigured panel shows
    /// nothing about translation at all.
    /// </summary>
    public BroadcastState? Translated { get; set; }

    public TranslationState Translation { get; set; } = new();
    public ObsState Obs { get; set; } = new();
    public SlidesState Slides { get; set; } = new();
    public TelegramState Telegram { get; set; } = new();
    public CaptureResetState CaptureReset { get; set; } = new();
    public List<PreflightItem> Preflight { get; set; } = new();
    public AuthState Auth { get; set; } = new();
    public List<StepState> Steps { get; set; } = new();
    public LastActionState? LastAction { get; set; }
    public bool Starting { get; set; }
    public DateTime ServerTime { get; set; }
}
