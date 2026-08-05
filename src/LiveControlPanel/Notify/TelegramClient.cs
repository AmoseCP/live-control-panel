using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LiveControlPanel.Notify;

public sealed record TelegramResult(bool Ok, string Message);

public interface ITelegramClient
{
    Task<TelegramResult> SendAsync(string botToken, string chatId, string text, CancellationToken ct = default);
}

/// <summary>
/// Bot API sendMessage over plain HttpClient (FR 5.4 — no third-party library needed).
///
/// Every stream is unlisted, so this notification is the primary way anyone finds the link. Failures
/// are returned as messages the operator can act on, never swallowed.
/// </summary>
public sealed class TelegramClient : ITelegramClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TelegramClient> _log;

    public TelegramClient(HttpClient http, ILogger<TelegramClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<TelegramResult> SendAsync(
        string botToken, string chatId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            return new TelegramResult(false, "Telegram 未配置：设置页缺少 Bot Token。");
        if (string.IsNullOrWhiteSpace(chatId))
            return new TelegramResult(false, "Telegram 未配置：设置页缺少群 ID。");

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var payload = new { chat_id = chatId, text, disable_web_page_preview = false };

        try
        {
            using var response = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return new TelegramResult(true, "已发送。");

            _log.LogWarning("Telegram sendMessage failed: {Status} {Body}", (int)response.StatusCode, body);
            return new TelegramResult(false, Explain(body));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram sendMessage threw");
            return new TelegramResult(false, "发送失败：无法连接 Telegram，请检查网络后重试。");
        }
    }

    /// <summary>
    /// Turns Bot API errors into instructions. FR 8 forbids showing raw technical text to operators,
    /// and chat-id mistakes are the single most common misconfiguration here.
    /// </summary>
    private static string Explain(string body)
    {
        var description = "";
        try { description = JsonNode.Parse(body)?["description"]?.GetValue<string>() ?? ""; }
        catch (Exception) { /* non-JSON body: fall through to the generic message */ }

        if (description.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            return "发送失败：找不到该群。请检查群 ID（群为负数，超级群以 -100 开头）。";

        if (description.Contains("bot was kicked", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not a member", StringComparison.OrdinalIgnoreCase))
            return "发送失败：机器人已不在群里，请重新把它加入群。";

        if (description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return "发送失败：Bot Token 无效，请在设置页重新填写。";

        if (description.Contains("not enough rights", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("restricted", StringComparison.OrdinalIgnoreCase))
            return "发送失败：机器人在群里没有发言权限，请在群设置中允许它发消息。";

        return "发送失败，请稍后重试。若持续失败请联系管理员。";
    }
}
