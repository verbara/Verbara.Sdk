using System.Text;
using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Internal;

/// <summary>
/// Frozen UTF-8 payloads from the unfenced source-generated request context. Each reachable request
/// DTO is covered, including null constructions that would throw under the strict read options.
/// These tests freeze what the context writes; they do not see which options a send site passes.
/// That wiring is held by the governance guard <c>SttSendContextGuardTests</c>.
/// </summary>
public sealed class SttRequestSerializationTests
{
    [Fact]
    public void Serialize_ShouldKeepGoogleRequestBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new GoogleSpeechRequest(), VoiceAiSttJsonContext.Default.GoogleSpeechRequest)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"config":{"encoding":"LINEAR16","sampleRateHertz":0,"languageCode":"es-CO","model":"default"},"audio":{"content":""}}"""));
        JsonSerializer.SerializeToUtf8Bytes(new GoogleSpeechRequest { Config = null!, Audio = null! }, VoiceAiSttJsonContext.Default.GoogleSpeechRequest)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"config":null,"audio":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepGoogleConfigBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new GoogleSpeechConfig(), VoiceAiSttJsonContext.Default.GoogleSpeechConfig)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"encoding":"LINEAR16","sampleRateHertz":0,"languageCode":"es-CO","model":"default"}"""));
        JsonSerializer.SerializeToUtf8Bytes(new GoogleSpeechConfig { Encoding = null!, LanguageCode = null!, Model = null! }, VoiceAiSttJsonContext.Default.GoogleSpeechConfig)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"encoding":null,"sampleRateHertz":0,"languageCode":null,"model":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepGoogleAudioBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new GoogleSpeechAudio { Content = "AQID" }, VoiceAiSttJsonContext.Default.GoogleSpeechAudio)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"content":"AQID"}"""));
        JsonSerializer.SerializeToUtf8Bytes(new GoogleSpeechAudio { Content = null! }, VoiceAiSttJsonContext.Default.GoogleSpeechAudio)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"content":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepSpeechmaticsStartBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsStartRecognitionMessage(), VoiceAiSttJsonContext.Default.SpeechmaticsStartRecognitionMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"message":"StartRecognition","audio_format":{"type":"raw","encoding":"pcm_s16le","sample_rate":0},"transcription_config":{"language":"","operating_point":"enhanced","enable_partials":false,"max_delay":0}}"""));
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsStartRecognitionMessage { Message = null!, AudioFormat = null!, TranscriptionConfig = null! },
            VoiceAiSttJsonContext.Default.SpeechmaticsStartRecognitionMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"message":null,"audio_format":null,"transcription_config":null}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepSpeechmaticsAudioFormatBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsAudioFormat(), VoiceAiSttJsonContext.Default.SpeechmaticsAudioFormat)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"type":"raw","encoding":"pcm_s16le","sample_rate":0}"""));
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsAudioFormat { Type = null!, Encoding = null! }, VoiceAiSttJsonContext.Default.SpeechmaticsAudioFormat)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"type":null,"encoding":null,"sample_rate":0}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepSpeechmaticsConfigBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsTranscriptionConfig(), VoiceAiSttJsonContext.Default.SpeechmaticsTranscriptionConfig)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"language":"","operating_point":"enhanced","enable_partials":false,"max_delay":0}"""));
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsTranscriptionConfig { Language = null!, OperatingPoint = null! },
            VoiceAiSttJsonContext.Default.SpeechmaticsTranscriptionConfig)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"language":null,"operating_point":null,"enable_partials":false,"max_delay":0}"""));
    }

    [Fact]
    public void Serialize_ShouldKeepSpeechmaticsEndBytes_WhenSerializedByTheRequestContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsEndOfStreamMessage { LastSeqNo = 7 }, VoiceAiSttJsonContext.Default.SpeechmaticsEndOfStreamMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"message":"EndOfStream","last_seq_no":7}"""));
        JsonSerializer.SerializeToUtf8Bytes(new SpeechmaticsEndOfStreamMessage { Message = null! }, VoiceAiSttJsonContext.Default.SpeechmaticsEndOfStreamMessage)
            .Should().Equal(Encoding.UTF8.GetBytes(
                """{"message":null,"last_seq_no":0}"""));
    }
}
