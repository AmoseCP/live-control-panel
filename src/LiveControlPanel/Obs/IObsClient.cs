namespace LiveControlPanel.Obs;

public sealed record ObsStatus(
    bool Connected,
    bool Streaming,
    long StreamTimeSeconds,
    string? CurrentScene,
    double DroppedFramesPercent,
    long KbitsPerSec,
    IReadOnlyList<string> Scenes);

/// <summary>
/// Why the OBS connection is not up. "OBS is not connected" is not actionable on its own: the usual
/// cause is that OBS is running with its WebSocket server switched off, and telling that operator to
/// "open OBS" sends them looking at the one thing that is already correct.
/// </summary>
public enum ObsProblem
{
    None = 0,

    /// <summary>Nothing is listening on the port — OBS closed, or its WebSocket server disabled.</summary>
    NotListening,

    /// <summary>Reached obs-websocket, but it rejected the password (close code 4009).</summary>
    AuthenticationFailed,

    /// <summary>The configured URL is not a usable WebSocket address.</summary>
    BadUrl,

    Other,
}

/// <summary>
/// The panel's view of OBS. An interface so the orchestrator and pre-flight can be tested without
/// a running OBS — and because FR 2.2 forbids the browser from talking to obs-websocket directly,
/// this is the only path.
/// </summary>
public interface IObsClient
{
    ObsStatus Status { get; }

    /// <summary>Why it is not connected, so the pre-flight can name the actual fix.</summary>
    ObsProblem Problem { get; }

    Task SetSceneAsync(string sceneName, CancellationToken ct = default);
    Task StartStreamAsync(CancellationToken ct = default);
    Task StopStreamAsync(CancellationToken ct = default);

    /// <summary>Names of every audio/video input OBS knows about.</summary>
    Task<IReadOnlyList<string>> GetInputNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Peak level activity seen for <paramref name="inputName"/> within <paramref name="window"/>,
    /// or null when no volume-meter data has arrived (OBS not connected, or input absent).
    /// </summary>
    double? GetRecentAudioPeak(string inputName, TimeSpan window);

    /// <summary>True when the named source is producing video on the program output.</summary>
    Task<bool?> IsSourceActiveAsync(string sourceName, CancellationToken ct = default);
}
