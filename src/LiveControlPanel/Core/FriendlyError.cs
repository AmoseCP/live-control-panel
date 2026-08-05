using System.Net;
using Google;
using LiveControlPanel.Obs;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Core;

/// <summary>
/// Translates exceptions into something an operator can act on. FR 8 is explicit: never show
/// "403 quotaExceeded" — show "today's limit for creating streams is used up, contact the
/// administrator".
/// </summary>
public static class FriendlyError
{
    public static Msg Describe(Exception ex) => ex switch
    {
        NotAuthorizedException => new Msg(
            "YouTube 授权已失效，需要重新授权后才能创建直播。请在设置页点「重新授权」。",
            "YouTube authorization has expired, so no broadcast can be created. Use \"re-authorize\" on " +
            "the settings page."),

        ObsUnavailableException => new Msg(
            "OBS 没有连上。请确认 OBS Studio 已经打开，然后重试这一步。",
            "OBS is not connected. Check that OBS Studio is open, then retry this step."),

        ObsRequestException obs => new Msg(
            $"OBS 拒绝了这个操作：{obs.Message}。请确认场景名称是否与设置页一致。",
            $"OBS refused the request: {obs.Message}. Check that the scene names match the settings page."),

        TimeoutException timeout when timeout is LocalizedTimeoutException localized => localized.Localized,

        TimeoutException => new Msg(
            "操作超时，请重试这一步。",
            "The operation timed out. Retry this step."),

        GoogleApiException google => DescribeGoogle(google),

        HttpRequestException => new Msg(
            "网络连不上。请检查网线或 WiFi，然后重试这一步。",
            "No network connection. Check the cable or WiFi, then retry this step."),

        TaskCanceledException => new Msg(
            "操作超时。请重试这一步。",
            "The operation timed out. Retry this step."),

        LocalizedInvalidOperationException localized => localized.Localized,

        InvalidOperationException invalid => Msg.Same(invalid.Message),

        _ => new Msg(
            "操作失败，请重试这一步。若持续失败请联系管理员。",
            "Something went wrong. Retry this step; if it keeps failing, contact the administrator."),
    };

    private static Msg DescribeGoogle(GoogleApiException ex)
    {
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason ?? "";

        if (reason.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return new Msg(
                "今天创建直播的次数已达上限，请联系管理员。",
                "Today's limit for creating broadcasts has been reached. Contact the administrator.");

        if (reason is "liveStreamingNotEnabled")
            return new Msg(
                "这个 YouTube 频道还没有开启直播功能，请联系管理员。",
                "Live streaming is not enabled on this YouTube channel. Contact the administrator.");

        if (reason is "invalidThumbnailImage" or "mediaBodyRequired")
            return new Msg(
                "封面图片无法上传，请联系管理员更换图片。本场直播不受影响。",
                "The thumbnail could not be uploaded. Ask the administrator to replace the image. This " +
                "broadcast is otherwise unaffected.");

        if (reason is "errorStreamInactive")
            return new Msg(
                "YouTube 还没有收到画面。请确认 OBS 已经开始推流，然后重试这一步。",
                "YouTube has not received any video yet. Check that OBS is streaming, then retry this step.");

        if (reason is "invalidTransition")
            return new Msg(
                "这场直播的状态无法这样切换，可能已经结束了。请刷新页面确认。",
                "This broadcast cannot change state that way — it may already have ended. Refresh the page.");

        return ex.HttpStatusCode switch
        {
            HttpStatusCode.Unauthorized => new Msg(
                "YouTube 授权已失效，请在设置页重新授权。",
                "YouTube authorization has expired. Re-authorize on the settings page."),
            HttpStatusCode.Forbidden => new Msg(
                "YouTube 拒绝了这个操作，请联系管理员。",
                "YouTube refused the request. Contact the administrator."),
            HttpStatusCode.NotFound => new Msg(
                "在 YouTube 上找不到这场直播，可能已被删除。请刷新页面确认。",
                "This broadcast no longer exists on YouTube — it may have been deleted. Refresh the page."),
            HttpStatusCode.TooManyRequests => new Msg(
                "操作太频繁，请等十几秒再重试。",
                "Too many requests. Wait about fifteen seconds and retry."),
            _ => new Msg(
                "YouTube 暂时无法完成这个操作，请重试这一步。若持续失败请联系管理员。",
                "YouTube could not complete the request. Retry this step; if it keeps failing, contact " +
                "the administrator."),
        };
    }
}

/// <summary>
/// An <see cref="InvalidOperationException"/> that already knows how to say itself in both languages.
/// Used where the panel itself detects a misconfiguration and has better wording than any generic
/// mapping could produce.
/// </summary>
public sealed class LocalizedInvalidOperationException : InvalidOperationException
{
    public LocalizedInvalidOperationException(Msg localized) : base(localized.Zh) => Localized = localized;

    public Msg Localized { get; }
}

public sealed class LocalizedTimeoutException : TimeoutException
{
    public LocalizedTimeoutException(Msg localized) : base(localized.Zh) => Localized = localized;

    public Msg Localized { get; }
}
