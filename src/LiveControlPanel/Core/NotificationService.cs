using LiveControlPanel.Config;
using LiveControlPanel.Notify;

namespace LiveControlPanel.Core;

/// <summary>
/// Telegram delivery for the current broadcast (FR 5.4).
///
/// Every stream is unlisted, so this message is the primary distribution channel for the link — it
/// gets critical-path treatment: idempotent, explicit about failure, always retryable.
/// </summary>
public sealed class NotificationService
{
    private readonly ConfigStore _config;
    private readonly StateManager _state;
    private readonly ITelegramClient _telegram;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NotificationService(ConfigStore config, StateManager state, ITelegramClient telegram)
    {
        _config = config;
        _state = state;
        _telegram = telegram;
    }

    /// <summary>
    /// Sends once per broadcast. Repeat calls report the existing timestamp without hitting the API —
    /// five taps must not put five messages in the group.
    /// </summary>
    public async Task<TelegramResult> SendCurrentAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sentAt = _state.Read(s => s.Telegram.SentAt);
            if (sentAt is not null)
                return new TelegramResult(true, $"已在 {sentAt:HH:mm} 发送过，未重复发送。");

            var broadcast = _state.Read(s => s.Broadcast);
            if (broadcast?.WatchUrl is null)
                return new TelegramResult(false, "还没有创建直播，暂时没有链接可以发送。");

            var settings = _config.Settings;
            var templateId = _state.Read(s => s.Today?.TemplateId);
            var template = templateId is null ? null : _config.FindTemplate(templateId);

            var pattern = !string.IsNullOrWhiteSpace(template?.TelegramMessage)
                ? template!.TelegramMessage!
                : settings.TelegramMessageDefault;

            var text = Render(pattern, broadcast.Title ?? "", broadcast.WatchUrl);

            var result = await _telegram
                .SendAsync(settings.TelegramBotToken, settings.TelegramChatId, text, ct)
                .ConfigureAwait(false);

            _state.Mutate(s =>
            {
                if (result.Ok)
                {
                    s.Telegram.SentAt = DateTime.Now;
                    s.Telegram.LastError = null;
                }
                else
                {
                    s.Telegram.LastError = result.Message;
                }
            });

            if (result.Ok) _state.RecordAction("发送 Telegram 通知", broadcast.Title);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Settings-page test send (FR 5.4): chat ids are easy to get wrong, so verify at deploy time.</summary>
    public Task<TelegramResult> SendTestAsync(CancellationToken ct = default)
    {
        var settings = _config.Settings;
        var text = Render(settings.TelegramMessageDefault, "测试消息 / Test message",
            "https://www.youtube.com/live/TEST");

        return _telegram.SendAsync(settings.TelegramBotToken, settings.TelegramChatId, text, ct);
    }

    internal static string Render(string pattern, string title, string url) =>
        (pattern ?? "").Replace("{title}", title).Replace("{url}", url);
}
