using System.Net;
using Google;

namespace LiveControlPanel.Youtube;

/// <summary>
/// Exponential backoff for YouTube calls (FR 8): network jitter must not abort the start-today
/// orchestration. Only retries transient conditions — a 403 quotaExceeded or a 400 will never
/// succeed on retry and retrying it just wastes the operator's time.
/// </summary>
public static class Retry
{
    public static async Task<T> TransientAsync<T>(
        Func<CancellationToken, Task<T>> action,
        ILogger log,
        CancellationToken ct,
        int attempts = 4)
    {
        var delay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                log.LogWarning(ex, "Transient YouTube failure (attempt {Attempt}/{Attempts}); retrying in {Delay}",
                    attempt, attempts, delay);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(8).Ticks));
            }
        }
    }

    public static Task TransientAsync(
        Func<CancellationToken, Task> action, ILogger log, CancellationToken ct, int attempts = 4) =>
        TransientAsync(async token => { await action(token).ConfigureAwait(false); return true; },
            log, ct, attempts);

    public static bool IsTransient(Exception ex) => ex switch
    {
        GoogleApiException google => google.HttpStatusCode is
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false,
    };
}
