using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class SessionReconcilerTests
{
    [Fact]
    public void MarkOrphaned_ShouldTransitionToFailed()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);

        SessionReconciler.TryMarkOrphaned(session);

        session.State.Should().Be(CallSessionState.Failed);
        session.Metadata.Should().ContainKey("cause");
        session.Metadata["cause"].Should().Be("orphaned");
    }

    [Fact]
    public void MarkTimedOut_ShouldTransitionToTimedOut_WhenDialing()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);

        SessionReconciler.TryMarkTimedOut(session);

        session.State.Should().Be(CallSessionState.TimedOut);
    }

    [Fact]
    public void MarkTimedOut_ShouldNotAffectConnectedSession()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);

        SessionReconciler.TryMarkTimedOut(session);

        session.State.Should().Be(CallSessionState.Connected);
    }
}
