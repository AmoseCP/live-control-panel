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
/// The panel's view of OBS. An interface so the orchestrator and pre-flight can be tested without
/// a running OBS — and because FR 2.2 forbids the browser from talking to obs-websocket directly,
/// this is the only path.
/// </summary>
public interface IObsClient
{
    ObsStatus Status { get; }

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
