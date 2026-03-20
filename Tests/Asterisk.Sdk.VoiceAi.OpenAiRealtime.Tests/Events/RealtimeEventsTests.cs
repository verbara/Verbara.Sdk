using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Events;

public class RealtimeEventsTests
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
}
