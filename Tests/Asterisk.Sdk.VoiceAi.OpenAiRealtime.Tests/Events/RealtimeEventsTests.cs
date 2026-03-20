using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Events;

public sealed class RealtimeEventsTests
{
    [Fact]
    public void RealtimeTranscriptEvent_IsARealtimeEvent()
    {
        var channelId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;
        RealtimeEvent evt = new RealtimeTranscriptEvent(channelId, ts, "hello", IsFinal: true);

        evt.ChannelId.Should().Be(channelId);
        evt.Timestamp.Should().Be(ts);
        evt.Should().BeOfType<RealtimeTranscriptEvent>();
    }

    [Fact]
    public void RealtimeResponseEndedEvent_ExposesChannelIdAndDuration()
    {
        var id = Guid.NewGuid();
        var duration = TimeSpan.FromSeconds(2.5);
        var evt = new RealtimeResponseEndedEvent(id, DateTimeOffset.UtcNow, duration);

        evt.ChannelId.Should().Be(id);
        evt.Duration.Should().Be(duration);
    }

    [Fact]
    public void RealtimeSpeechStartedEvent_ExposesChannelId()
    {
        var id = Guid.NewGuid();
        var evt = new RealtimeSpeechStartedEvent(id, DateTimeOffset.UtcNow);
        evt.ChannelId.Should().Be(id);
    }

    [Fact]
    public void RealtimeSpeechStoppedEvent_ExposesChannelId()
    {
        var id = Guid.NewGuid();
        var evt = new RealtimeSpeechStoppedEvent(id, DateTimeOffset.UtcNow);
        evt.ChannelId.Should().Be(id);
    }

    [Fact]
    public void RealtimeResponseStartedEvent_ExposesChannelId()
    {
        var id = Guid.NewGuid();
        var evt = new RealtimeResponseStartedEvent(id, DateTimeOffset.UtcNow);
        evt.ChannelId.Should().Be(id);
    }

    [Fact]
    public void RealtimeFunctionCalledEvent_ExposesAllProperties()
    {
        var id = Guid.NewGuid();
        var evt = new RealtimeFunctionCalledEvent(
            id, DateTimeOffset.UtcNow,
            "get_time", """{"zone":"UTC"}""", """{"time":"12:00"}""");

        evt.FunctionName.Should().Be("get_time");
        evt.ArgumentsJson.Should().Be("""{"zone":"UTC"}""");
        evt.ResultJson.Should().Be("""{"time":"12:00"}""");
    }

    [Fact]
    public void RealtimeErrorEvent_ExposesMessage()
    {
        var id = Guid.NewGuid();
        var evt = new RealtimeErrorEvent(id, DateTimeOffset.UtcNow, "rate limited");
        evt.Message.Should().Be("rate limited");
    }
}
