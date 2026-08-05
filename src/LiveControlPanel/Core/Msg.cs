namespace LiveControlPanel.Core;

/// <summary>
/// An operator-facing string in both languages.
///
/// Messages carry both rather than being localized per request because state reaches the page over a
/// WebSocket push. Localizing server-side would mean the language was fixed when the socket opened,
/// so switching would need a reconnect, and the PC and an iPad watching the same panel could not be
/// in different languages. Seven people share this machine; they do not share a language preference.
///
/// The payload cost is one extra short string per message on a state snapshot of a couple of KB.
/// </summary>
public sealed record Msg(string Zh, string En)
{
    public static readonly Msg Empty = new("", "");

    /// <summary>Lets a call site write <c>("中文", "English")</c> wherever a <see cref="Msg"/> is expected.</summary>
    public static implicit operator Msg((string Zh, string En) pair) => new(pair.Zh, pair.En);

    /// <summary>For text that is identical in both languages — a title, an id, a number.</summary>
    public static Msg Same(string text) => new(text, text);

    /// <summary>Not serialized — it would ride along on every message on the wire for no reason.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Zh) && string.IsNullOrEmpty(En);

    /// <summary>Picks one language. Server-side use only, for logs.</summary>
    public string For(string language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? En : Zh;

    public override string ToString() => Zh;
}
