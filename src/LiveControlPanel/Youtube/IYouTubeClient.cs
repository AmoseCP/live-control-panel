namespace LiveControlPanel.Youtube;

public sealed record BroadcastInfo(string Id, string Title, string LifeCycleStatus, string WatchUrl);

public sealed record StreamKeyInfo(string StreamId, string IngestionKey, string IngestionAddress);

public sealed record AuthInfo(bool Valid, int? ExpiresInDays, DateTime? AuthorizedAt, string? Message);

public sealed record CreateBroadcastRequest(
    string Title,
    string Description,
    DateTime ScheduledStart,
    string PrivacyStatus,
    bool MadeForKids,
    string LatencyPreference);

/// <summary>
/// The YouTube operations the panel needs. An interface so orchestration and pre-flight logic are
/// testable — none of the tests may touch the live API.
/// </summary>
public interface IYouTubeClient
{
    Task<AuthInfo> GetAuthInfoAsync(CancellationToken ct = default);

    Task<BroadcastInfo> CreateBroadcastAsync(CreateBroadcastRequest request, CancellationToken ct = default);

    Task BindStreamAsync(string broadcastId, string streamId, CancellationToken ct = default);

    Task SetThumbnailAsync(string broadcastId, Stream image, string contentType, CancellationToken ct = default);

    Task<string?> GetLifeCycleStatusAsync(string broadcastId, CancellationToken ct = default);

    Task TransitionToCompleteAsync(string broadcastId, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts still in a live-ish state. FR 4.4's highest-risk pre-flight item: on Wednesday and
    /// Friday two services share one stream key, and the evening operator must not collide with a
    /// morning broadcast that was never ended.
    /// </summary>
    Task<IReadOnlyList<BroadcastInfo>> ListUnfinishedBroadcastsAsync(CancellationToken ct = default);

    Task<StreamKeyInfo> CreateReusableStreamAsync(string title, CancellationToken ct = default);

    /// <summary>Watch URL for a broadcast id. FR 4.2: no "?feature=share" suffix.</summary>
    static string WatchUrl(string broadcastId) => $"https://www.youtube.com/live/{broadcastId}";
}
