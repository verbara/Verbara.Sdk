using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// <see cref="StatusEvent"/> carries the channel state and caller headers every supported version sends:
/// <c>ChannelState</c>, <c>ChannelStateDesc</c>, <c>CallerIDNum</c> and <c>CallerIDName</c>.
/// </summary>
public sealed class StatusEventStateAndCallerWireTests
{
    public static TheoryData<string> Versions() => StatusWireReplies.Versions();

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Deserialize_ShouldReadTheStateAndCallerHeaders_WhenTheFrameIsACapturedStatus(string version)
    {
        var frames = await StatusWireReplies.ParseAsync(version);

        frames.Should().HaveCount(6, $"Asterisk {version} listed six legs");
        using var scope = new AssertionScope();
        foreach (var frame in frames)
        {
            var at = $"{frame.Channel} on Asterisk {version}";
            frame.ChannelState.Should().Be(StatusWireReplies.Header(frame, "ChannelState"), at);
            frame.ChannelStateDesc.Should().Be(StatusWireReplies.Header(frame, "ChannelStateDesc"), at);
            frame.CallerIdNum.Should().Be(StatusWireReplies.Header(frame, "CallerIDNum"), at);
            frame.CallerIdName.Should().Be(StatusWireReplies.Header(frame, "CallerIDName"), at);
        }

        var ringing = frames.Single(f => f.Channel == "Local/200@d5probe-00000001;1");
        ringing.ChannelState.Should().Be("5", "the leg running AppDial2 is ringing");
        ringing.ChannelStateDesc.Should().Be("Ringing");

        var ring = frames.Single(f => f.Channel == "Local/200@d5probe-00000001;2");
        ring.ChannelState.Should().Be("4", "the leg in Wait on the unanswered extension is in Ring");
        ring.ChannelStateDesc.Should().Be("Ring");
    }
}
