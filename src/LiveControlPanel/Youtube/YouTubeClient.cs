using System.Runtime.Versioning;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace LiveControlPanel.Youtube;

/// <summary>YouTube Data API v3 calls with the exact parameters FR 4.2 mandates.</summary>
[SupportedOSPlatform("windows")]
public sealed class YouTubeClient : IYouTubeClient, IDisposable
{
    private const string ApplicationName = "LiveControlPanel";

    private readonly YouTubeAuth _auth;
    private readonly ILogger<YouTubeClient> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private YouTubeService? _service;
    private object? _serviceCredential;

    public YouTubeClient(YouTubeAuth auth, ILogger<YouTubeClient> log)
    {
        _auth = auth;
        _log = log;
    }

    public Task<AuthInfo> GetAuthInfoAsync(CancellationToken ct = default) => _auth.GetAuthInfoAsync(ct);

    public async Task<BroadcastInfo> CreateBroadcastAsync(CreateBroadcastRequest request, CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        var broadcast = new LiveBroadcast
        {
            Snippet = new LiveBroadcastSnippet
            {
                Title = request.Title,
                Description = request.Description,
                ScheduledStartTimeDateTimeOffset = new DateTimeOffset(request.ScheduledStart.ToUniversalTime()),
            },
            Status = new LiveBroadcastStatus
            {
                PrivacyStatus = request.PrivacyStatus,
                SelfDeclaredMadeForKids = request.MadeForKids,
            },
            ContentDetails = new LiveBroadcastContentDetails
            {
                EnableAutoStart = true,
                EnableAutoStop = false,
                LatencyPreference = request.LatencyPreference,
                // Ultra-low latency does not support DVR, and the monitor stream only adds delay.
                EnableDvr = false,
                MonitorStream = new MonitorStreamInfo { EnableMonitorStream = false },
            },
        };

        var created = await Retry.TransientAsync(
            token => service.LiveBroadcasts
                .Insert(broadcast, Parts("snippet", "status", "contentDetails"))
                .ExecuteAsync(token),
            _log, ct).ConfigureAwait(false);

        _log.LogInformation("Created broadcast {Id} \"{Title}\"", created.Id, request.Title);

        return new BroadcastInfo(
            created.Id,
            created.Snippet?.Title ?? request.Title,
            created.Status?.LifeCycleStatus ?? "created",
            IYouTubeClient.WatchUrl(created.Id));
    }

    public async Task BindStreamAsync(string broadcastId, string streamId, CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        await Retry.TransientAsync(async token =>
        {
            var bind = service.LiveBroadcasts.Bind(broadcastId, Parts("id", "contentDetails"));
            bind.StreamId = streamId;
            await bind.ExecuteAsync(token).ConfigureAwait(false);
        }, _log, ct).ConfigureAwait(false);

        _log.LogInformation("Bound broadcast {Id} to stream {StreamId}", broadcastId, streamId);
    }

    public async Task SetThumbnailAsync(
        string broadcastId, Stream image, string contentType, CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        var upload = service.Thumbnails.Set(broadcastId, image, contentType);
        var progress = await upload.UploadAsync(ct).ConfigureAwait(false);

        if (progress.Status != UploadStatus.Completed)
            throw progress.Exception ?? new InvalidOperationException("Thumbnail upload did not complete.");

        _log.LogInformation("Uploaded thumbnail for broadcast {Id}", broadcastId);
    }

    public async Task<string?> GetLifeCycleStatusAsync(string broadcastId, CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        var response = await Retry.TransientAsync(token =>
        {
            var list = service.LiveBroadcasts.List(Parts("id", "status"));
            list.Id = broadcastId;
            return list.ExecuteAsync(token);
        }, _log, ct).ConfigureAwait(false);

        return response.Items?.FirstOrDefault()?.Status?.LifeCycleStatus;
    }

    public async Task TransitionToCompleteAsync(string broadcastId, CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        await Retry.TransientAsync(token => service.LiveBroadcasts
            .Transition(
                LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Complete,
                broadcastId,
                Parts("id", "status"))
            .ExecuteAsync(token), _log, ct).ConfigureAwait(false);

        _log.LogInformation("Transitioned broadcast {Id} to complete", broadcastId);
    }

    public async Task<IReadOnlyList<BroadcastInfo>> ListUnfinishedBroadcastsAsync(CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        var results = new List<BroadcastInfo>();

        // "active" covers testing/live; "upcoming" catches a broadcast that was created and bound
        // but never started, which holds the stream key just the same.
        foreach (var status in new[]
                 {
                     LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Active,
                     LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Upcoming,
                 })
        {
            var response = await Retry.TransientAsync(token =>
            {
                var list = service.LiveBroadcasts.List(Parts("id", "snippet", "status"));
                list.BroadcastStatus = status;
                list.MaxResults = 20;
                return list.ExecuteAsync(token);
            }, _log, ct).ConfigureAwait(false);

            foreach (var item in response.Items ?? new List<LiveBroadcast>())
            {
                var lifeCycle = item.Status?.LifeCycleStatus ?? "";
                if (lifeCycle is "complete" or "revoked") continue;
                if (results.Any(r => r.Id == item.Id)) continue;

                results.Add(new BroadcastInfo(
                    item.Id,
                    item.Snippet?.Title ?? item.Id,
                    lifeCycle,
                    IYouTubeClient.WatchUrl(item.Id)));
            }
        }

        return results;
    }

    public async Task<StreamKeyInfo> CreateReusableStreamAsync(string title, CancellationToken ct = default)
    {
        var service = await ServiceAsync(ct).ConfigureAwait(false);

        var stream = new LiveStream
        {
            Snippet = new LiveStreamSnippet { Title = title },
            Cdn = new CdnSettings
            {
                IngestionType = "rtmp",
                // "variable" lets OBS decide; the key then never needs changing again (FR 5.2).
                Resolution = "variable",
                FrameRate = "variable",
            },
        };

        var created = await Retry.TransientAsync(
            token => service.LiveStreams.Insert(stream, Parts("snippet", "cdn")).ExecuteAsync(token),
            _log, ct).ConfigureAwait(false);

        _log.LogInformation("Created reusable stream {Id}", created.Id);

        return new StreamKeyInfo(
            created.Id,
            created.Cdn?.IngestionInfo?.StreamName ?? "",
            created.Cdn?.IngestionInfo?.IngestionAddress ?? "");
    }

    private static Repeatable<string> Parts(params string[] parts) => new(parts);

    /// <summary>Cached service, rebuilt when the credential changes (re-authorization).</summary>
    private async Task<YouTubeService> ServiceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var credential = await _auth.TryGetCredentialAsync(ct).ConfigureAwait(false)
                ?? throw new NotAuthorizedException();

            if (_service is not null && ReferenceEquals(_serviceCredential, credential)) return _service;

            _service?.Dispose();
            _service = new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
            _serviceCredential = credential;
            return _service;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _service?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>No usable YouTube authorization. Mapped to a re-authorize prompt in the UI.</summary>
public sealed class NotAuthorizedException : Exception
{
    public NotAuthorizedException() : base("YouTube authorization is missing or expired.") { }
}
