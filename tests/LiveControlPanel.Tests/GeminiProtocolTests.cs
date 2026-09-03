using System.Text.Json.Nodes;
using LiveControlPanel.Translate;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The wire format of the live-translation session.
///
/// This is pinned by tests rather than trusted because the whole feature rests on one payload: get
/// translationConfig wrong and the model happily returns the speaker's own words, which sounds like
/// success right up to the point a congregation is listening to it.
/// </summary>
public sealed class GeminiProtocolTests
{
    private static JsonNode Setup(string target = "en", bool echo = true, string model = "m") =>
        JsonNode.Parse(GeminiProtocol.BuildSetup(new GeminiSessionOptions(model, target, echo)))!;

    [Fact]
    public void Setup_asks_for_audio_out_and_both_transcripts()
    {
        var config = Setup()["setup"]!["generationConfig"]!;

        Assert.Equal("AUDIO", config["responseModalities"]!.AsArray()[0]!.GetValue<string>());
        Assert.NotNull(config["inputAudioTranscription"]);
        Assert.NotNull(config["outputAudioTranscription"]);
    }

    [Fact]
    public void Setup_carries_the_target_language_and_echo_flag()
    {
        var translation = Setup("zh-CN", echo: false)["setup"]!["generationConfig"]!["translationConfig"]!;

        Assert.Equal("zh-CN", translation["targetLanguageCode"]!.GetValue<string>());
        Assert.False(translation["echoTargetLanguage"]!.GetValue<bool>());
    }

    /// <summary>
    /// No source language is declared anywhere. That is the point: the speaker may move between
    /// Chinese and English inside one sermon, and the model detects it per utterance.
    /// </summary>
    [Fact]
    public void Setup_never_declares_a_source_language()
    {
        var translation = Setup()["setup"]!["generationConfig"]!["translationConfig"]!.AsObject();

        Assert.DoesNotContain("sourceLanguageCode", translation.Select(p => p.Key));
    }

    [Fact]
    public void Setup_defaults_a_blank_target_language_to_english()
    {
        var translation = Setup("   ")["setup"]!["generationConfig"]!["translationConfig"]!;

        Assert.Equal("en", translation["targetLanguageCode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("gemini-3.5-live-translate-preview", "models/gemini-3.5-live-translate-preview")]
    [InlineData("models/gemini-3.5-live-translate-preview", "models/gemini-3.5-live-translate-preview")]
    [InlineData("  gemini-x  ", "models/gemini-x")]
    public void Model_ids_are_accepted_in_either_documented_form(string input, string expected) =>
        Assert.Equal(expected, GeminiProtocol.NormalizeModel(input));

    [Fact]
    public void A_blank_model_falls_back_to_the_translation_model()
    {
        Assert.Equal("models/gemini-3.5-live-translate-preview", GeminiProtocol.NormalizeModel(""));
        Assert.Equal("models/gemini-3.5-live-translate-preview", GeminiProtocol.NormalizeModel("   "));
    }

    [Fact]
    public void An_audio_chunk_is_base64_pcm_at_the_declared_rate()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var audio = JsonNode.Parse(GeminiProtocol.BuildAudioChunk(pcm))!["realtimeInput"]!["audio"]!;

        Assert.Equal(Convert.ToBase64String(pcm), audio["data"]!.GetValue<string>());
        Assert.Equal("audio/pcm;rate=16000", audio["mimeType"]!.GetValue<string>());
    }

    /// <summary>100 ms of 16 kHz mono 16-bit. The API's chunk size is not negotiable.</summary>
    [Fact]
    public void The_chunk_size_is_a_hundred_milliseconds()
    {
        Assert.Equal(3200, GeminiProtocol.ChunkBytes);
        Assert.Equal(100, GeminiProtocol.ChunkDuration.TotalMilliseconds);
    }

    [Fact]
    public void Setup_complete_is_recognised()
    {
        var message = GeminiProtocol.Parse("""{"setupComplete":{}}""");

        Assert.NotNull(message);
        Assert.True(message!.SetupComplete);
    }

    [Fact]
    public void Go_away_is_recognised()
    {
        var message = GeminiProtocol.Parse("""{"goAway":{"timeLeft":"5s"}}""");

        Assert.NotNull(message);
        Assert.True(message!.GoAway);
    }

    [Fact]
    public void Audio_parts_are_decoded_and_transcripts_come_through()
    {
        var pcm = new byte[] { 9, 8, 7, 6 };
        var json = $$"""
        {
          "serverContent": {
            "inputTranscription": { "text": "大家好" },
            "outputTranscription": { "text": "Hello everyone" },
            "modelTurn": { "parts": [ { "inlineData": { "mimeType": "audio/pcm", "data": "{{Convert.ToBase64String(pcm)}}" } } ] }
          }
        }
        """;

        var message = GeminiProtocol.Parse(json);

        Assert.NotNull(message);
        Assert.Equal(pcm, message!.Audio);
        Assert.Equal("大家好", message.InputTranscript);
        Assert.Equal("Hello everyone", message.OutputTranscript);
    }

    /// <summary>
    /// A turn arrives as several parts. Concatenating them in order is what keeps the translated
    /// voice from developing a stutter.
    /// </summary>
    [Fact]
    public void Multiple_audio_parts_are_concatenated_in_order()
    {
        var first = new byte[] { 1, 2 };
        var second = new byte[] { 3, 4, 5 };
        var json = $$"""
        {
          "serverContent": { "modelTurn": { "parts": [
            { "inlineData": { "mimeType": "audio/pcm", "data": "{{Convert.ToBase64String(first)}}" } },
            { "inlineData": { "mimeType": "audio/pcm", "data": "{{Convert.ToBase64String(second)}}" } }
          ] } }
        }
        """;

        var message = GeminiProtocol.Parse(json);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, message!.Audio);
    }

    [Fact]
    public void Non_audio_parts_are_ignored()
    {
        const string json = """
        {"serverContent":{"modelTurn":{"parts":[{"text":"hello"},{"inlineData":{"mimeType":"image/png","data":"AAAA"}}]}}}
        """;

        Assert.Null(GeminiProtocol.Parse(json));
    }

    [Fact]
    public void A_server_error_is_surfaced()
    {
        var message = GeminiProtocol.Parse("""{"error":{"message":"quota exceeded"}}""");

        Assert.Equal("quota exceeded", message!.Error);
    }

    /// <summary>
    /// The socket stays open for a whole service. A single frame that is not JSON — or is JSON the
    /// panel has no use for — must not end it, so both decode to "nothing happened".
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"serverContent":{}}""")]
    public void Unusable_frames_decode_to_nothing(string json) => Assert.Null(GeminiProtocol.Parse(json));

    [Fact]
    public void A_corrupt_audio_part_is_dropped_rather_than_thrown()
    {
        const string json = """
        {"serverContent":{"outputTranscription":{"text":"ok"},"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm","data":"!!!not base64!!!"}}]}}}
        """;

        var message = GeminiProtocol.Parse(json);

        Assert.NotNull(message);
        Assert.Null(message!.Audio);
        Assert.Equal("ok", message.OutputTranscript);
    }

    /// <summary>
    /// Valid JSON of the wrong shape is exactly as unusable as text that is not JSON, and this
    /// socket stays open for a whole service. The accessors throw on a mistyped node — GetValue
    /// &lt;string&gt;() on a number, an indexer on an array — and those used to escape Parse, surface
    /// as "translation failed" and tear the session down.
    /// </summary>
    [Theory]
    [InlineData("""{"error":"a bare string, not an object"}""")]
    [InlineData("""{"error":{"message":12345}}""")]
    [InlineData("""{"serverContent":{"outputTranscription":{"text":42}}}""")]
    [InlineData("""{"serverContent":{"modelTurn":{"parts":"not an array"}}}""")]
    [InlineData("""{"serverContent":{"modelTurn":{"parts":["not an object"]}}}""")]
    [InlineData("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":7,"data":"AA=="}}]}}}""")]
    [InlineData("""{"serverContent":[1,2,3]}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""42""")]
    public void A_frame_of_the_wrong_shape_is_survivable(string json)
    {
        var exception = Record.Exception(() => GeminiProtocol.Parse(json));

        Assert.Null(exception);
    }

    [Fact]
    public void The_endpoint_url_escapes_the_key()
    {
        var uri = GeminiProtocol.Endpoint("a b&c");

        Assert.Equal("wss", uri.Scheme);
        Assert.Contains("key=a%20b%26c", uri.Query);
    }
}
