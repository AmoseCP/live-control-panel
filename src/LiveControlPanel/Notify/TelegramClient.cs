using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LiveControlPanel.Core;

namespace LiveControlPanel.Notify;

public sealed record TelegramResult(bool Ok, Msg Message);

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
            return new TelegramResult(false, new Msg(
                "Telegram 未配置：设置页缺少 Bot Token。",
                "Telegram is not configured: the bot token is missing on the settings page."));
        if (string.IsNullOrWhiteSpace(chatId))
            return new TelegramResult(false, new Msg(
                "Telegram 未配置：设置页缺少群 ID。",
                "Telegram is not configured: the group id is missing on the settings page."));

        // Trimmed, because a bot token is pasted from BotFather and a group id from getUpdates, and
        // both routinely arrive with a trailing newline or space. IsNullOrWhiteSpace passes those,
        // and the token then goes into the URL — where PostAsJsonAsync throws UriFormatException,
        // which the catch below reported as "cannot reach Telegram. Check the network." A trailing
        // space is one of the most common misconfigurations here, and that message sent whoever hit
        // it off to debug the church WiFi.
        botToken = botToken.Trim();
        chatId = chatId.Trim();

        if (!IsPlausibleToken(botToken))
            return new TelegramResult(false, new Msg(
                "Telegram Bot Token 的格式不对。它应该形如 123456789:AA... —— 请从 BotFather 重新复制，" +
                "注意不要带上多余的空格或换行。",
                "The Telegram bot token is malformed. It should look like 123456789:AA… — copy it again " +
                "from BotFather, taking care not to include stray spaces or line breaks."));

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var payload = new { chat_id = chatId, text, disable_web_page_preview = false };

        try
        {
            using var response = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return new TelegramResult(true, new Msg("已发送。", "Sent."));

            _log.LogWarning("Telegram sendMessage failed: {Status} {Body}", (int)response.StatusCode, body);
            return new TelegramResult(false, Explain(body));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram sendMessage threw");
            return new TelegramResult(false, new Msg(
                "发送失败：无法连接 Telegram，请检查网络后重试。",
                "Send failed: cannot reach Telegram. Check the network and retry."));
        }
    }

    /// <summary>
    /// Turns Bot API errors into instructions. FR 8 forbids showing raw technical text to operators,
    /// and chat-id mistakes are the single most common misconfiguration here.
    /// </summary>
    /// <summary>
    /// A token's shape, not its validity: digits, a colon, then the secret. Anything that cannot go
    /// into a URL is caught here rather than surfacing as a connectivity problem.
    /// </summary>
    private static bool IsPlausibleToken(string token)
    {
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1) return false;

        foreach (var c in token)
        {
            if (char.IsWhiteSpace(c) || c == '/' || c == '?' || c == '#') return false;
        }

        return token[..colon].All(char.IsAsciiDigit);
    }

    private static Msg Explain(string body)
    {
        var description = "";
        try { description = JsonNode.Parse(body)?["description"]?.GetValue<string>() ?? ""; }
        catch (Exception) { /* non-JSON body: fall through to the generic message */ }

        if (description.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            return new Msg(
                "发送失败：找不到该群。请检查群 ID（群为负数，超级群以 -100 开头）。",
                "Send failed: group not found. Check the group id (groups are negative; supergroups start " +
                "with -100).");

        if (description.Contains("bot was kicked", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not a member", StringComparison.OrdinalIgnoreCase))
            return new Msg(
                "发送失败：机器人已不在群里，请重新把它加入群。",
                "Send failed: the bot is no longer in the group. Add it back.");

        if (description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return new Msg(
                "发送失败：Bot Token 无效，请在设置页重新填写。",
                "Send failed: the bot token is invalid. Re-enter it on the settings page.");

        if (description.Contains("not enough rights", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("restricted", StringComparison.OrdinalIgnoreCase))
            return new Msg(
                "发送失败：机器人在群里没有发言权限，请在群设置中允许它发消息。",
                "Send failed: the bot is not allowed to post in the group. Grant it permission to send " +
                "messages.");

        return new Msg(
            "发送失败，请稍后重试。若持续失败请联系管理员。",
            "Send failed. Try again shortly; if it keeps failing, contact the administrator.");
    }
}
