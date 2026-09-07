using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveControlPanel.Translate;

/// <summary>What one live-translation session is asked to do.</summary>
public sealed record GeminiSessionOptions(string Model, string TargetLanguage, bool EchoTargetLanguage);

/// <summary>
/// One decoded server frame. Every field is independently optional because a single frame may carry
/// audio, a transcript, both, or neither.
/// </summary>
public sealed record GeminiMessage
{
    /// <summary>Raw 24 kHz mono 16-bit PCM, already concatenated across the frame's parts.</summary>
    public byte[]? Audio { get; init; }

    /// <summary>What the model heard, in the source language.</summary>
    public string? InputTranscript { get; init; }

    /// <summary>What the model said, in the target language.</summary>
    public string? OutputTranscript { get; init; }

    public bool SetupComplete { get; init; }

    /// <summary>The server is about to close this session; reconnect rather than wait for the drop.</summary>
    public bool GoAway { get; init; }

    /// <summary>A server-reported error, verbatim. Surfaced to the log, never to the operator.</summary>
    public string? Error { get; init; }

    public bool IsEmpty =>
        Audio is null && InputTranscript is null && OutputTranscript is null
        && !SetupComplete && !GoAway && Error is null;
}

/// <summary>
/// The wire format of the Gemini Live API's BidiGenerateContent socket, kept free of any I/O so the
/// message shapes can be pinned by tests. Nothing in this file talks to the network.
///
/// Audio in is 16 kHz mono little-endian 16-bit PCM; audio out is 24 kHz mono 16-bit PCM. Those two
/// rates are fixed by the API, which is why they are constants here rather than settings.
/// </summary>
public static class GeminiProtocol
{
    public const int InputSampleRate = 16000;
    public const int OutputSampleRate = 24000;

    /// <summary>Chunk length the API expects audio to arrive in.</summary>
    public static readonly TimeSpan ChunkDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>3200 bytes: 100 ms of 16 kHz mono 16-bit.</summary>
    public const int ChunkBytes = InputSampleRate / 10 * 2;

    private const string Host =
        "wss://generativelanguage.googleapis.com/ws/" +
        "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    public static Uri Endpoint(string apiKey) => new($"{Host}?key={Uri.EscapeDataString(apiKey)}");

    /// <summary>
    /// Model ids are written both ways in Google's own documentation. Normalising here means an
    /// administrator can paste either form into the settings page and neither fails at 04:40.
    /// </summary>
    public static string NormalizeModel(string model)
    {
        var trimmed = (model ?? "").Trim();
        if (trimmed.Length == 0) return "models/gemini-3.5-live-translate-preview";
        return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? trimmed : "models/" + trimmed;
    }

    /// <summary>
    /// The opening frame. No source language is declared: the model detects it, which is what lets a
    /// speaker move between Chinese and English inside one sermon. echoTargetLanguage keeps the
    /// second broadcast speaking rather than falling silent when the speaker is already in the
    /// target language.
    /// </summary>
    public static string BuildSetup(GeminiSessionOptions options)
    {
        var setup = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = NormalizeModel(options.Model),
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("AUDIO"),
                    ["inputAudioTranscription"] = new JsonObject(),
                    ["outputAudioTranscription"] = new JsonObject(),
                    ["translationConfig"] = new JsonObject
                    {
                        ["targetLanguageCode"] = string.IsNullOrWhiteSpace(options.TargetLanguage)
                            ? "en"
                            : options.TargetLanguage.Trim(),
                        ["echoTargetLanguage"] = options.EchoTargetLanguage,
                    },
                },
            },
        };

        return setup.ToJsonString();
    }

    public static string BuildAudioChunk(ReadOnlySpan<byte> pcm16kMono) => new JsonObject
    {
        ["realtimeInput"] = new JsonObject
        {
            ["audio"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(pcm16kMono),
                ["mimeType"] = $"audio/pcm;rate={InputSampleRate}",
            },
        },
    }.ToJsonString();

    /// <summary>
    /// Decodes one server frame. Returns null for a frame that is not JSON at all — the socket is
    /// long-lived and a single unparsable frame must not end a service.
    /// </summary>
    public static GeminiMessage? Parse(string json)
    {
        // Everything below runs inside one catch on purpose. Parsing is only half the risk: the
        // accessors themselves throw when a node is not the shape they expect — GetValue<string>()
        // on a number, an indexer on an array — and this socket stays open for a whole service. The
        // promise in the summary above is "a single unusable frame must not end it", and a frame
        // that is valid JSON of the wrong shape is exactly as unusable as one that is not JSON.
        try
        {
            return ParseCore(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GeminiMessage? ParseCore(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is null) return null;

        var error = root["error"]?["message"]?.GetValue<string>()
            ?? (root["error"] is not null ? root["error"]!.ToJsonString() : null);

        var content = root["serverContent"];

        byte[]? audio = null;
        var parts = content?["modelTurn"]?["parts"]?.AsArray();
        if (parts is not null)
        {
            List<byte[]>? chunks = null;
            foreach (var part in parts)
            {
                var inline = part?["inlineData"];
                var mime = inline?["mimeType"]?.GetValue<string>() ?? "";
                if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;

                var data = inline?["data"]?.GetValue<string>();
                if (string.IsNullOrEmpty(data)) continue;

                try { (chunks ??= new List<byte[]>()).Add(Convert.FromBase64String(data)); }
                catch (FormatException) { /* a corrupt part is dropped, not fatal */ }
            }

            if (chunks is { Count: > 0 })
            {
                audio = chunks.Count == 1 ? chunks[0] : Concat(chunks);
            }
        }

        var message = new GeminiMessage
        {
            Audio = audio,
            InputTranscript = Text(content?["inputTranscription"]?["text"]),
            OutputTranscript = Text(content?["outputTranscription"]?["text"]),
            SetupComplete = root["setupComplete"] is not null,
            GoAway = root["goAway"] is not null,
            Error = error,
        };

        return message.IsEmpty ? null : message;
    }

    private static string? Text(JsonNode? node)
    {
        var value = node?.GetValue<string>();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static byte[] Concat(List<byte[]> chunks)
    {
        var total = 0;
        foreach (var chunk in chunks) total += chunk.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }
        return result;
    }
}
