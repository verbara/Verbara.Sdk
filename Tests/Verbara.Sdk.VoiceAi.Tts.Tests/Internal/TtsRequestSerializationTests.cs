using System.Text;
using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Internal;

/// <summary>
/// Frozen UTF-8 literals for every reachable TTS request DTO, including null constructions that the
/// original context permits. These freeze the context's output; send-site options wiring is held by
/// the governance guard. Value-only DTOs have populated/default or true/false controls instead.
/// </summary>
public sealed class TtsRequestSerializationTests
{
    [Fact]
    public void Serialize_ShouldKeepElevenLabsTextChunkBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new ElevenLabsTextChunk { Text = "hello", Flush = true, VoiceSettings = new ElevenLabsVoiceSettings { Stability = 0.25f, SimilarityBoost = 0.75f } }, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"text":"hello","flush":true,"voice_settings":{"stability":0.25,"similarity_boost":0.75}}"""));
        JsonSerializer.SerializeToUtf8Bytes(new ElevenLabsTextChunk { Text = null! }, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"text":null,"flush":null,"voice_settings":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepElevenLabsVoiceSettingsBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new ElevenLabsVoiceSettings { Stability = 0.25f, SimilarityBoost = 0.75f }, VoiceAiTtsJsonContext.Default.ElevenLabsVoiceSettings)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"stability":0.25,"similarity_boost":0.75}"""));
        JsonSerializer.SerializeToUtf8Bytes(new ElevenLabsVoiceSettings(), VoiceAiTtsJsonContext.Default.ElevenLabsVoiceSettings)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"stability":0,"similarity_boost":0}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepCartesiaRequestBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new CartesiaTtsRequest(), VoiceAiTtsJsonContext.Default.CartesiaTtsRequest)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"model_id":"","voice":{"mode":"id","id":""},"output_format":{"container":"raw","encoding":"pcm_s16le","sample_rate":0},"language":"","transcript":"","context_id":"","continue":null}"""));
        JsonSerializer.SerializeToUtf8Bytes(new CartesiaTtsRequest { ModelId = null!, Voice = null!, OutputFormat = null!, Language = null!, Transcript = null!, ContextId = null! }, VoiceAiTtsJsonContext.Default.CartesiaTtsRequest)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"model_id":null,"voice":null,"output_format":null,"language":null,"transcript":null,"context_id":null,"continue":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepCartesiaVoiceBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new CartesiaTtsVoice(), VoiceAiTtsJsonContext.Default.CartesiaTtsVoice)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"mode":"id","id":""}"""));
        JsonSerializer.SerializeToUtf8Bytes(new CartesiaTtsVoice { Mode = null!, Id = null! }, VoiceAiTtsJsonContext.Default.CartesiaTtsVoice)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"mode":null,"id":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepCartesiaOutputFormatBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new CartesiaTtsOutputFormat(), VoiceAiTtsJsonContext.Default.CartesiaTtsOutputFormat)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"container":"raw","encoding":"pcm_s16le","sample_rate":0}"""));
        JsonSerializer.SerializeToUtf8Bytes(new CartesiaTtsOutputFormat { Container = null!, Encoding = null! }, VoiceAiTtsJsonContext.Default.CartesiaTtsOutputFormat)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"container":null,"encoding":null,"sample_rate":0}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepSpeechmaticsRequestBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsTtsRequest { Text = "hello", Language = "en", SampleRate = 16000 }, VoiceAiTtsJsonContext.Default.SpeechmaticsTtsRequest)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"text":"hello","language":"en","sample_rate":16000}"""));
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsTtsRequest { Text = null!, Language = null! }, VoiceAiTtsJsonContext.Default.SpeechmaticsTtsRequest)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"text":null,"language":null,"sample_rate":0}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepDeepgramSpeakBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new DeepgramSpeakMessage { Text = "hello" }, VoiceAiTtsJsonContext.Default.DeepgramSpeakMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"type":"Speak","text":"hello"}"""));
        JsonSerializer.SerializeToUtf8Bytes(new DeepgramSpeakMessage { Type = null!, Text = null! }, VoiceAiTtsJsonContext.Default.DeepgramSpeakMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"type":null,"text":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepDeepgramControlBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new DeepgramControlMessage { Type = "Flush" }, VoiceAiTtsJsonContext.Default.DeepgramControlMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"type":"Flush"}"""));
        JsonSerializer.SerializeToUtf8Bytes(new DeepgramControlMessage { Type = null! }, VoiceAiTtsJsonContext.Default.DeepgramControlMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"type":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepLmntInitBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new LmntInitMessage(), VoiceAiTtsJsonContext.Default.LmntInitMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"X-API-Key":"","voice":"","format":"","sample_rate":0,"language":"","speed":1}"""));
        JsonSerializer.SerializeToUtf8Bytes(new LmntInitMessage { ApiKey = null!, Voice = null!, Format = null!, Language = null! }, VoiceAiTtsJsonContext.Default.LmntInitMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"X-API-Key":null,"voice":null,"format":null,"sample_rate":0,"language":null,"speed":1}"""));
        JsonSerializer.SerializeToUtf8Bytes(new LmntInitMessage { Model = "blizzard" }, VoiceAiTtsJsonContext.Default.LmntInitMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"X-API-Key":"","voice":"","format":"","sample_rate":0,"language":"","speed":1,"model":"blizzard"}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepLmntTextBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new LmntTextMessage { Text = "hello" }, VoiceAiTtsJsonContext.Default.LmntTextMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"text":"hello"}"""));
        JsonSerializer.SerializeToUtf8Bytes(new LmntTextMessage { Text = null! }, VoiceAiTtsJsonContext.Default.LmntTextMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"text":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepLmntFlushBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(LmntFlushMessage.Instance, VoiceAiTtsJsonContext.Default.LmntFlushMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"flush":true}"""));
        JsonSerializer.SerializeToUtf8Bytes(new LmntFlushMessage { Flush = false }, VoiceAiTtsJsonContext.Default.LmntFlushMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"flush":false}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepLmntEofBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(LmntEofMessage.Instance, VoiceAiTtsJsonContext.Default.LmntEofMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"eof":true}"""));
        JsonSerializer.SerializeToUtf8Bytes(new LmntEofMessage { Eof = false }, VoiceAiTtsJsonContext.Default.LmntEofMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"eof":false}"""));
    }
}
