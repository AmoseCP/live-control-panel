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
    public static string Describe(Exception ex) => ex switch
    {
        NotAuthorizedException =>
            "YouTube 授权已失效，需要重新授权后才能创建直播。请在设置页点「重新授权」。",

        ObsUnavailableException =>
            "OBS 没有连上。请确认 OBS Studio 已经打开，然后重试这一步。",

        ObsRequestException obs =>
            $"OBS 拒绝了这个操作：{obs.Message}。请确认场景名称是否与设置页一致。",

        TimeoutException timeout => timeout.Message,

        GoogleApiException google => DescribeGoogle(google),

        HttpRequestException =>
            "网络连不上。请检查网线或 WiFi，然后重试这一步。",

        TaskCanceledException =>
            "操作超时。请重试这一步。",

        InvalidOperationException invalid => invalid.Message,

        _ => "操作失败，请重试这一步。若持续失败请联系管理员。",
    };

    private static string DescribeGoogle(GoogleApiException ex)
    {
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason ?? "";

        if (reason.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return "今天创建直播的次数已达上限，请联系管理员。";

        if (reason is "liveStreamingNotEnabled")
            return "这个 YouTube 频道还没有开启直播功能，请联系管理员。";

        if (reason is "invalidThumbnailImage" or "mediaBodyRequired")
            return "封面图片无法上传，请联系管理员更换图片。本场直播不受影响。";

        if (reason is "errorStreamInactive")
            return "YouTube 还没有收到画面。请确认 OBS 已经开始推流，然后重试这一步。";

        if (reason is "invalidTransition")
            return "这场直播的状态无法这样切换，可能已经结束了。请刷新页面确认。";

        return ex.HttpStatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "YouTube 授权已失效，请在设置页重新授权。",
            HttpStatusCode.Forbidden =>
                "YouTube 拒绝了这个操作，请联系管理员。",
            HttpStatusCode.NotFound =>
                "在 YouTube 上找不到这场直播，可能已被删除。请刷新页面确认。",
            HttpStatusCode.TooManyRequests =>
                "操作太频繁，请等十几秒再重试。",
            _ => "YouTube 暂时无法完成这个操作，请重试这一步。若持续失败请联系管理员。",
        };
    }
}
