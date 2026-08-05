using LiveControlPanel.Config;
using LiveControlPanel.Obs;
using LiveControlPanel.Slides;

namespace LiveControlPanel.Core;

/// <summary>
/// Owns the single in-memory <see cref="RuntimeState"/> (FR 3.4) and pushes it after every change.
///
/// Nothing here is persisted on purpose: FR 2.2 requires that stopping the panel never interrupt a
/// stream, and a restart re-derives everything it can from OBS and YouTube.
/// </summary>
public sealed class StateManager
{
    private readonly ConfigStore _config;
    private readonly StateHub _hub;
    private readonly ISlideController _slides;
    private readonly ILogger<StateManager> _log;
    private readonly object _gate = new();
    private readonly RuntimeState _state = new();

    public StateManager(ConfigStore config, StateHub hub, ISlideController slides, ILogger<StateManager> log)
    {
        _config = config;
        _hub = hub;
        _slides = slides;
        _log = log;
    }

    /// <summary>A snapshot safe to serialize while other threads mutate state.</summary>
    public RuntimeState Snapshot()
    {
        lock (_gate)
        {
            RefreshScheduleLocked(DateTime.Now);
            _state.ServerTime = DateTime.Now;
            return Clone(_state);
        }
    }

    public void Mutate(Action<RuntimeState> change)
    {
        RuntimeState snapshot;
        lock (_gate)
        {
            change(_state);
            RefreshScheduleLocked(DateTime.Now);
            _state.ServerTime = DateTime.Now;
            snapshot = Clone(_state);
        }
        _hub.Broadcast(snapshot);
    }

    /// <summary>Reads state without publishing. For callers that only need to decide something.</summary>
    public T Read<T>(Func<RuntimeState, T> read)
    {
        lock (_gate) return read(_state);
    }

    public void RecordAction(Msg what, string? service = null) =>
        Mutate(s => s.LastAction = new LastActionState { At = DateTime.Now, What = what, Service = service });

    public void ApplyObsStatus(ObsStatus status) => Mutate(s =>
    {
        s.Obs.Connected = status.Connected;
        s.Obs.Streaming = status.Streaming;
        s.Obs.StreamTimeSeconds = status.StreamTimeSeconds;
        s.Obs.CurrentScene = status.CurrentScene;
        s.Obs.DroppedFramesPercent = status.DroppedFramesPercent;
        s.Obs.KbitsPerSec = status.KbitsPerSec;
        s.Obs.Scenes = status.Scenes.ToList();
    });

    public void RefreshSlides()
    {
        SlidesState slides;
        try
        {
            slides = _slides.GetState();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Reading slide state failed");
            slides = new SlidesState { Available = false };
        }

        Mutate(s => s.Slides = slides);
    }

    /// <summary>
    /// Recomputes today/nextService/phase. FR 6.1: the UI shows only actions that make sense, so an
    /// accurate phase is what keeps "stop streaming" off the screen when nothing is live.
    /// </summary>
    private void RefreshScheduleLocked(DateTime now)
    {
        var templates = _config.SchedulableTemplates().ToList();
        var window = _config.Settings.MatchWindow;

        // An explicit choice outranks the calendar. The operator picked this service on purpose —
        // possibly for an ad-hoc stream outside every window — so neither a match nor the absence of
        // one may touch it. Only StartAnother clears it.
        if (_state.Today?.Manual != true)
        {
            var match = ScheduleMatcher.MatchToday(templates, now, window);

            _state.Today = match is null
                ? null
                : new TodayState
                {
                    TemplateId = match.Template.Id,
                    Title = match.Title,
                    ScheduledStart = match.ScheduledStart,
                };
        }

        if (_state.Today is null)
        {
            var next = ScheduleMatcher.NextService(templates, now);
            _state.NextService = next is null
                ? null
                : new NextServiceState
                {
                    Title = next.Title,
                    StartsAt = next.ScheduledStart,
                    TemplateId = next.Template.Id,
                };
        }
        else
        {
            _state.NextService = null;
        }

        _state.Phase = DerivePhase();
    }

    private string DerivePhase()
    {
        var broadcast = _state.Broadcast;

        if (broadcast is not null)
        {
            if (broadcast.Status == BroadcastStatus.Complete) return Phase.Ended;
            if (_state.Obs.Streaming || broadcast.Status is BroadcastStatus.Live or BroadcastStatus.Testing)
                return Phase.Live;
            return Phase.Ready;
        }

        return _state.Today is not null ? Phase.Ready : Phase.NoSchedule;
    }

    /// <summary>
    /// Serializing through JSON keeps the snapshot honest as the state graph grows — a hand-written
    /// copy is one forgotten field away from leaking a mutable reference to the hub.
    /// </summary>
    private static RuntimeState Clone(RuntimeState state) =>
        System.Text.Json.JsonSerializer.Deserialize<RuntimeState>(
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options), Json.Options)!;
}
