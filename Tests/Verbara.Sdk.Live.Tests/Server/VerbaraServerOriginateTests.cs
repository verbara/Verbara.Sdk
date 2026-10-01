using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// <see cref="VerbaraServer.OriginateAsync"/> over a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/>, against
/// a peer that answers an async originate as Asterisk 20.20.1, 22.9.0 and 23.4.1 do: the action's
/// <c>Response: Success</c>, then one <c>OriginateResponse</c> whose <c>Response</c> header is the originate's outcome.
/// The connection took that event for a second response and dropped it, so every originate Asterisk accepted ended in
/// an <see cref="OperationCanceledException"/> once the connection's event timeout ran out.
/// </summary>
/// <remarks>
/// The peer writes the <c>OriginateResponse</c> at once, so the connection's <c>DefaultEventTimeout</c>, set here to
/// <see cref="EventTimeout"/>, is far above what the outcome takes to arrive; <see cref="Run.Bound"/> is a hang bound.
/// The mocks in <c>VerbaraServerExtendedTests</c> cover the same method through the interface and stay as they are.
/// </remarks>
public sealed class VerbaraServerOriginateTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task OriginateAsync_ShouldReturnSuccess_WhenAsteriskReportsOriginateResponseSuccess()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, OriginateOutcome = "Success" };
        await using var run = await Run.ConnectAsync(peer, eventTimeout: EventTimeout);

        OriginateResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await run.Server
            .OriginateAsync("Local/s@hold", "hold", "s", timeout: TimeSpan.FromSeconds(30)).AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            error.Should().BeNull("Asterisk accepted the originate and reported its outcome");
            result?.Success.Should().BeTrue("the OriginateResponse says Success");
            result?.Message.Should().Be("Success");
            result?.ChannelId.Should().Be(BootingAsterisk.OriginatedChannel, "the outcome names the originated channel");
            peer.Asked("Originate").Should().Be(1);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task OriginateAsync_ShouldReturnFailure_WhenAsteriskReportsOriginateResponseFailure()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, OriginateOutcome = "Failure" };
        await using var run = await Run.ConnectAsync(peer, eventTimeout: EventTimeout);

        OriginateResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await run.Server
            .OriginateAsync("Nosuchtech/x", "hold", "s", timeout: TimeSpan.FromSeconds(30)).AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            error.Should().BeNull("Asterisk accepted the originate and reported its outcome, a failure");
            result?.Success.Should().BeFalse("the OriginateResponse says Failure");
            result?.Message.Should().Be("Failure");
            peer.Asked("Originate").Should().Be(1);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }
}
