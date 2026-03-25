using Asterisk.Sdk.VoiceAi;
using Asterisk.Sdk.VoiceAi.Events;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tests.Pipeline;

public sealed class VoiceAiEventTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    [Fact]
    public void ConversationTurn_ShouldExposeAllProperties()
    {
        var turn = new ConversationTurn("Hello", "Hi there", Ts);

        turn.UserTranscript.Should().Be("Hello");
        turn.AssistantResponse.Should().Be("Hi there");
        turn.Timestamp.Should().Be(Ts);
    }

    [Fact]
    public void ConversationTurn_ShouldSupportValueEquality()
    {
        var a = new ConversationTurn("Hello", "Hi", Ts);
        var b = new ConversationTurn("Hello", "Hi", Ts);

        a.Should().Be(b);
    }

    [Fact]
    public void SpeechEndedEvent_ShouldExposeDuration()
    {
        var duration = TimeSpan.FromSeconds(3.5);
        var evt = new SpeechEndedEvent(Ts, duration);

        evt.Timestamp.Should().Be(Ts);
        evt.Duration.Should().Be(duration);
        evt.Should().BeAssignableTo<VoiceAiPipelineEvent>();
    }

    [Fact]
    public void SynthesisEndedEvent_ShouldExposeDuration()
    {
        var duration = TimeSpan.FromSeconds(1.2);
        var evt = new SynthesisEndedEvent(Ts, duration);

        evt.Duration.Should().Be(duration);
        evt.Should().BeAssignableTo<VoiceAiPipelineEvent>();
    }

    [Fact]
    public void TranscriptReceivedEvent_ShouldExposeAllProperties()
    {
        var evt = new TranscriptReceivedEvent(Ts, "hello world", 0.95f, true);

        evt.Transcript.Should().Be("hello world");
        evt.Confidence.Should().BeApproximately(0.95f, 0.001f);
        evt.IsFinal.Should().BeTrue();
    }

    [Fact]
    public void TranscriptReceivedEvent_PartialTranscript_ShouldNotBeFinal()
    {
        var evt = new TranscriptReceivedEvent(Ts, "hel", 0.6f, false);

        evt.IsFinal.Should().BeFalse();
    }

    [Fact]
    public void PipelineErrorEvent_ShouldExposeAllProperties()
    {
        var ex = new InvalidOperationException("test");
        var evt = new PipelineErrorEvent(Ts, "STT failed", ex, PipelineErrorSource.Stt);

        evt.Message.Should().Be("STT failed");
        evt.Exception.Should().BeSameAs(ex);
        evt.Source.Should().Be(PipelineErrorSource.Stt);
    }

    [Fact]
    public void PipelineErrorEvent_ShouldAllowNullException()
    {
        var evt = new PipelineErrorEvent(Ts, "timeout", null, PipelineErrorSource.Tts);

        evt.Exception.Should().BeNull();
        evt.Source.Should().Be(PipelineErrorSource.Tts);
    }

    [Fact]
    public void SpeechRecognitionResult_ShouldExposeAllProperties()
    {
        var result = new SpeechRecognitionResult("hello", 0.99f, true, TimeSpan.FromSeconds(2));

        result.Transcript.Should().Be("hello");
        result.Confidence.Should().BeApproximately(0.99f, 0.001f);
        result.IsFinal.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SpeechRecognitionResult_ShouldSupportValueEquality()
    {
        var dur = TimeSpan.FromMilliseconds(500);
        var a = new SpeechRecognitionResult("hi", 0.8f, false, dur);
        var b = new SpeechRecognitionResult("hi", 0.8f, false, dur);

        a.Should().Be(b);
    }

    [Theory]
    [InlineData(PipelineErrorSource.Stt)]
    [InlineData(PipelineErrorSource.Tts)]
    [InlineData(PipelineErrorSource.Handler)]
    public void PipelineErrorSource_ShouldHaveAllMembers(PipelineErrorSource source)
    {
        Enum.IsDefined(source).Should().BeTrue();
    }
}
