using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// What <see cref="StatusEvent"/> reads from the frames four supported Asterisk versions really send.
/// </summary>
public sealed class StatusEventExtensionWireTests
{
    /// <summary>The members no supported version sends a header for.</summary>
    private static readonly string[] NeverSentHeaders =
        ["State", "CallerId", "Account", "BridgedChannel", "Link", "BridgedUniqueId"];

    public static TheoryData<string> Versions() => StatusWireReplies.Versions();

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Deserialize_ShouldReadExtensionFromTheExtenHeader_WhenTheFrameIsACapturedStatus(string version)
    {
        var frames = await StatusWireReplies.ParseAsync(version);

        frames.Should().HaveCount(6, $"Asterisk {version} listed six legs");
        using var scope = new AssertionScope();
        foreach (var frame in frames)
        {
            var exten = StatusWireReplies.Header(frame, "Exten");
            exten.Should().NotBeNull($"every captured frame carries an Exten header ({frame.Channel})");
            frame.Extension.Should().Be(exten,
                $"the frame of {frame.Channel} on Asterisk {version} sends the dialplan extension as Exten");
        }

        var dialled = frames.Single(f => f.Channel == "Local/100@d5probe-00000002;1");
        dialled.Extension.Should().Be(string.Empty,
            "the dialled leg running AppDial sends an empty Exten, which is an empty extension, not an absent one");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Deserialize_ShouldLeaveTheNeverSentMembersNull_WhenTheFrameIsACapturedStatus(string version)
    {
        var frames = await StatusWireReplies.ParseAsync(version);

        frames.Should().HaveCount(6);
        using var scope = new AssertionScope();
        foreach (var frame in frames)
        {
            frame.State.Should().BeNull();
            frame.CallerId.Should().BeNull();
            frame.Account.Should().BeNull();
            frame.BridgedChannel.Should().BeNull();
            frame.Link.Should().BeNull();
            frame.BridgedUniqueId.Should().BeNull();
            frame.RawFields.Should().NotBeNull();
            frame.RawFields!.Keys.Should().NotContain(
                key => NeverSentHeaders.Contains(key, StringComparer.OrdinalIgnoreCase),
                $"no supported version sends these headers ({frame.Channel}, Asterisk {version})");
        }
    }
}
