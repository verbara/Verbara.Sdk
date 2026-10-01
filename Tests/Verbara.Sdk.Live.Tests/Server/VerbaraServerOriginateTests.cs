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
/// Except in the late-outcome test, the peer writes the <c>OriginateResponse</c> at once, so the connection's
/// <c>DefaultEventTimeout</c>, set there to <see cref="EventTimeout"/>, is far above what the outcome takes to arrive;
/// <see cref="Run.Bound"/> is a hang bound.
/// The mocks in <c>VerbaraServerExtendedTests</c> cover the same method through the interface and stay as they are.
/// </remarks>
public sealed class VerbaraServerOriginateTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The connection's event timeout for the late-outcome test: far below <see cref="LateOutcome"/>.</summary>
    private static readonly TimeSpan ShortEventTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after the action's response the late outcome arrives: four times <see cref="ShortEventTimeout"/>.</summary>
    private static readonly TimeSpan LateOutcome = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// A destination that rings longer than the connection's event timeout. The originate's own <c>Timeout</c> is the
    /// time Asterisk lets it ring, so the wait for its outcome is bounded by that <c>Timeout</c> plus the connection's
    /// <c>DefaultResponseTimeout</c> (the owner's ruling D3 (c), 2026-09-30), not by <c>DefaultEventTimeout</c>: the
    /// outcome that arrives after the event timeout but within the originate's <c>Timeout</c> is returned. Here the
    /// event timeout is <see cref="ShortEventTimeout"/> and the outcome comes <see cref="LateOutcome"/> after the
    /// action's response, inside a 30 s <c>Timeout</c>.
    /// </summary>
    [Fact]
    public async Task OriginateAsync_ShouldReturnTheOutcome_WhenItArrivesAfterTheEventTimeoutButWithinTheOriginateTimeout()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, OriginateOutcome = "Success", OriginateRings = LateOutcome };
        await using var run = await Run.ConnectAsync(peer, eventTimeout: ShortEventTimeout);

        OriginateResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await run.Server
            .OriginateAsync("Local/s@wait8", "wait8", "s", timeout: TimeSpan.FromSeconds(30)).AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            error.Should().BeNull(
                "the outcome arrived within the originate's own Timeout, which bounds the wait, not the event timeout");
            result?.Success.Should().BeTrue("the OriginateResponse says Success");
            result?.Message.Should().Be("Success");
            result?.ChannelId.Should().Be(BootingAsterisk.OriginatedChannel, "the outcome names the originated channel");
            peer.Asked("Originate").Should().Be(1);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }
}
