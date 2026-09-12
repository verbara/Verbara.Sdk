using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Verbara.Sdk.Sessions.Tests;

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
    public void TryMarkTimedOut_ShouldTransitionToTimedOut_WhenRinging()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Ringing);

        var marked = SessionReconciler.TryMarkTimedOut(session);

        marked.Should().BeTrue();
        session.State.Should().Be(CallSessionState.TimedOut);
    }

    [Fact]
    public void TryMarkTimedOut_ShouldLeaveSessionQueued_WhenStateIsQueued()
    {
        // Queued -> TimedOut is a legal transition, so only the Dialing/Ringing guard keeps
        // the reconciler from timing out a caller who is waiting in a queue.
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Queued);

        var marked = SessionReconciler.TryMarkTimedOut(session);

        marked.Should().BeFalse();
        session.State.Should().Be(CallSessionState.Queued);
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
