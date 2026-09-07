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

    /// <summary>
    /// Time source for schedule matching, day rollover and ServerTime. Injectable because the
    /// rollover rules cannot be exercised against the wall clock; production uses the real time.
    /// </summary>
    internal Func<DateTime> Clock = () => DateTime.Now;

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
            var now = Clock();
            RefreshScheduleLocked(now);
            _state.ServerTime = now;
            return Clone(_state);
        }
    }

    public void Mutate(Action<RuntimeState> change)
    {
        RuntimeState snapshot;
        lock (_gate)
        {
            var now = Clock();
            change(_state);
            RefreshScheduleLocked(now);
            _state.ServerTime = now;
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
        s.Obs.Reconnecting = status.Reconnecting;
        s.Obs.Congestion = status.Congestion;
    });

    /// <summary>
    /// How long the presentation program is given to answer before it is called unavailable.
    ///
    /// Every COM call into WPS/PowerPoint is an unbounded, blocking, cross-apartment call with no
    /// message filter. If that program stops pumping — busy rendering a large slide, a modal save or
    /// licence dialog, a wedged add-in — this used to block the background loop outright, which also
    /// stopped the OBS status refresh and the authorization countdown, and left the operator with no
    /// fix but restarting the panel mid-service.
    ///
    /// A bounded wait cannot un-block the COM call itself; that thread stays parked until the other
    /// program recovers. What it does buy is that the panel keeps running and keeps saying something
    /// true — "slides unavailable" — instead of freezing everything behind the one feature that is
    /// explicitly optional.
    /// </summary>
    private static readonly TimeSpan SlideReadTimeout = TimeSpan.FromSeconds(3);

    public void RefreshSlides()
    {
        SlidesState slides;
        try
        {
            var read = Task.Run(() => _slides.GetState());

            slides = read.Wait(SlideReadTimeout) && read.IsCompletedSuccessfully
                ? read.Result
                : Unavailable(read);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Reading slide state failed");
            slides = new SlidesState { Available = false };
        }

        Mutate(s => s.Slides = slides);
    }

    private SlidesState Unavailable(Task<SlidesState> read)
    {
        if (!read.IsCompleted)
        {
            _log.LogWarning(
                "The presentation program did not answer within {Timeout}; reporting slides unavailable",
                SlideReadTimeout);
        }
        else
        {
            _log.LogDebug(read.Exception, "Reading slide state failed");
        }

        // Observed, so a faulted read does not surface later as an unobserved task exception.
        _ = read.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

        return new SlidesState { Available = false, Enabled = _config.Settings.Slides.Enabled };
    }

    /// <summary>
    /// Recomputes today/nextService/phase. FR 6.1: the UI shows only actions that make sense, so an
    /// accurate phase is what keeps "stop streaming" off the screen when nothing is live.
    /// </summary>
    private void RefreshScheduleLocked(DateTime now)
    {
        var templates = _config.SchedulableTemplates().ToList();
        var window = _config.Settings.MatchWindow;

        // Day rollover. The panel runs for weeks as a logon task, so yesterday's finished work must
        // retire on its own: Thursday's 04:40 operator has to meet a fresh, auto-matched Ready
        // screen, not Wednesday evening's "已结束" needing an extra tap (FR 6.1, zero training).
        // Retirement is keyed to the broadcast's own creation date — the auto-matched Today empties
        // itself when the match window lapses, hours before midnight, so a Today-based check would
        // never fire for a scheduled service. This also retires a start that failed partway
        // (created/bound) and was abandoned; the leftover on YouTube's side is the pre-flight's
        // job to flag, but it must never be silently reused as the next day's broadcast.
        // A broadcast that is on air — by its status or by OBS actually pushing — is never touched.
        if (_state.Broadcast is { } broadcast && broadcast.CreatedOn.Date < now.Date
            && broadcast.Status is not (BroadcastStatus.Live or BroadcastStatus.Testing)
            && !_state.Obs.Streaming)
        {
            _state.Broadcast = null;
            _state.Today = null;
            _state.Steps = new List<StepState>();
            _state.Telegram = new TelegramState();

            // Retired with the rest of yesterday's work. The note beside the button renders only
            // HH:mm, so Wednesday's 04:41 reset read as "already tried this morning" to Thursday's
            // operator — who then did not try it, which is precisely the opposite of what the field
            // is for.
            _state.CaptureReset.LastResetAt = null;
        }
        else if (_state.Today?.ScheduledStart is { } scheduled && scheduled.Date < now.Date
            && _state.Broadcast is null && _state.Today.Manual)
        {
            // A manual pick that was never started (no broadcast to date it by).
            _state.Today = null;
        }

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
        RefreshCaptureResetLocked();
    }

    /// <summary>
    /// Mirrors the capture-reset configuration onto the pushed state, so the operator page can show
    /// the button only where it exists. Kept here rather than set once at startup because settings
    /// are editable while the panel runs, and a button that appears only after a restart would be
    /// reported as "the setting did not save".
    /// </summary>
    private void RefreshCaptureResetLocked()
    {
        var settings = _config.Settings.CaptureReset;

        _state.CaptureReset.Enabled =
            settings.Enabled && !string.IsNullOrWhiteSpace(settings.DeviceInstanceId);

        _state.CaptureReset.DeviceName =
            string.IsNullOrWhiteSpace(settings.DeviceName) ? null : settings.DeviceName;
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
