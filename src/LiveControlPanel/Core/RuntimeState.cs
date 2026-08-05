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
}

public sealed class BroadcastState
{
    public string? Id { get; set; }
    public string? WatchUrl { get; set; }
    public string Status { get; set; } = BroadcastStatus.Created;
    public string? Title { get; set; }

    /// <summary>Orchestration bookkeeping — the basis for idempotency (FR 4.2).</summary>
    public bool ThumbnailUploaded { get; set; }
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
}

public sealed class SlidesState
{
    public bool Available { get; set; }
    public int? Current { get; set; }
    public int? Total { get; set; }
}

public sealed class TelegramState
{
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class PreflightItem
{
    public string Key { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";

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
    public string Name { get; set; } = "";

    /// <summary>pending | running | done | skipped | failed</summary>
    public string Status { get; set; } = "pending";

    public string? Message { get; set; }
}

public sealed class LastActionState
{
    public DateTime At { get; set; }
    public string What { get; set; } = "";
    public string? Service { get; set; }
}

/// <summary>FR 3.4. Held in memory only and pushed over the WebSocket; never persisted.</summary>
public sealed class RuntimeState
{
    public string Phase { get; set; } = Core.Phase.NoSchedule;
    public TodayState? Today { get; set; }
    public NextServiceState? NextService { get; set; }
    public BroadcastState? Broadcast { get; set; }
    public ObsState Obs { get; set; } = new();
    public SlidesState Slides { get; set; } = new();
    public TelegramState Telegram { get; set; } = new();
    public List<PreflightItem> Preflight { get; set; } = new();
    public AuthState Auth { get; set; } = new();
    public List<StepState> Steps { get; set; } = new();
    public LastActionState? LastAction { get; set; }
    public bool Starting { get; set; }
    public DateTime ServerTime { get; set; }
}
