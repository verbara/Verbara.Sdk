using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.FunctionalTests;

public sealed class ReconciliationTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void Reconciler_ShouldMarkOrphaned_WhenChannelsGoneWithoutHangup()
    {
        // Create a session but never send hangup events
        _fixture.SimulateNewChannel("rc-orphan-1", "PJSIP/trunk-001",
            ChannelState.Ring, linkedId: "rc-linked-1", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-1")!;
        session.State.Should().Be(CallSessionState.Created);

        // Reconciler marks as orphaned (Created -> Failed is valid)
        var result = SessionReconciler.TryMarkOrphaned(session);

        result.Should().BeTrue();
        session.State.Should().Be(CallSessionState.Failed);
        session.Metadata.Should().ContainKey("cause");
        session.Metadata["cause"].Should().Be("orphaned");
        session.Events.Should().Contain(e => e.Type == CallSessionEventType.Failed
            && e.Detail == "orphaned");
    }

    [Fact]
    public void Reconciler_ShouldRespectDialingTimeout()
    {
        _fixture.SimulateNewChannel("rc-dial-1", "PJSIP/100-001",
            ChannelState.Ring, linkedId: "rc-linked-2", context: "from-internal");
        _fixture.SimulateNewChannel("rc-dial-2", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: "rc-linked-2");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-2")!;

        // Transition to Dialing
        _fixture.SimulateDialBegin("rc-dial-1", "rc-dial-2", "PJSIP/trunk-002");
        session.State.Should().Be(CallSessionState.Dialing);

        // Reconciler times out dialing sessions
        var result = SessionReconciler.TryMarkTimedOut(session);

        result.Should().BeTrue();
        session.State.Should().Be(CallSessionState.TimedOut);
        session.Events.Should().Contain(e => e.Type == CallSessionEventType.TimedOut);
    }

    [Fact]
    public void Reconciler_ShouldNotTouchActiveSessions_WhenChannelsAlive()
    {
        var (_, _, _) = _fixture.SimulateInboundCallAnswered(
            callerUid: "rc-act-caller", agentUid: "rc-act-agent",
            linkedId: "rc-linked-3", bridgeId: "rc-br-3");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-3")!;
        session.State.Should().Be(CallSessionState.Connected);

        // TryMarkOrphaned should fail — Connected -> Failed is valid in transitions,
        // but TryMarkTimedOut checks for Dialing/Ringing only
        var timedOut = SessionReconciler.TryMarkTimedOut(session);
        timedOut.Should().BeFalse();
        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void Reconciler_ShouldNotModifyCompletedSessions()
    {
        _fixture.SimulateNewChannel("rc-done-1", "PJSIP/trunk-003",
            ChannelState.Ring, linkedId: "rc-linked-4", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-4")!;

        // Complete the session normally
        _fixture.SimulateHangup("rc-done-1", HangupCause.NormalClearing);
        session.State.Should().Be(CallSessionState.Failed); // Created -> Failed (no answer)

        // Try to orphan it again -- should fail (Failed is terminal)
        var orphaned = SessionReconciler.TryMarkOrphaned(session);
        orphaned.Should().BeFalse();

        var timedOut = SessionReconciler.TryMarkTimedOut(session);
        timedOut.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentEvents_ShouldMaintainConsistentIndices_WhenBurstOf50()
    {
        const int count = 50;
        var tasks = new Task[count];

        for (var i = 0; i < count; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(() =>
            {
                var uid = $"rc-burst-{idx:D3}";
                var linked = $"rc-burst-linked-{idx:D3}";
                _fixture.SimulateNewChannel(uid, $"PJSIP/trunk-{idx:D3}",
                    ChannelState.Ring, linkedId: linked, context: "from-trunk");
            });
        }

        await Task.WhenAll(tasks);

        // All 50 sessions should exist with correct indices
        for (var i = 0; i < count; i++)
        {
            var uid = $"rc-burst-{i:D3}";
            var linked = $"rc-burst-linked-{i:D3}";

            var byChannel = _fixture.SessionManager.GetByChannelId(uid);
            byChannel.Should().NotBeNull($"session for channel {uid} should exist");

            var byLinked = _fixture.SessionManager.GetByLinkedId(linked);
            byLinked.Should().NotBeNull($"session for linkedId {linked} should exist");
            byLinked.Should().BeSameAs(byChannel);
        }

        _fixture.SessionManager.ActiveSessions.Count().Should().BeGreaterOrEqualTo(count);
    }

    [Fact]
    public void Reconnection_ShouldCleanSessions_WhenServerReconnects()
    {
        // Create an active session
        _fixture.SimulateNewChannel("rc-recon-1", "PJSIP/trunk-010",
            ChannelState.Ring, linkedId: "rc-linked-5", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-5")!;
        session.State.Should().Be(CallSessionState.Created);

        // Detach simulates what happens on reconnect — manager detaches from server
        _fixture.SessionManager.DetachFromServer("test-srv");

        // After detach, new events won't update sessions
        // The session still exists in the store but won't receive updates
        _fixture.SessionManager.GetByLinkedId("rc-linked-5").Should().NotBeNull();

        // Re-attach
        _fixture.SessionManager.AttachToServer(_fixture.Server, "test-srv");

        // New channels after re-attach should work normally
        _fixture.SimulateNewChannel("rc-recon-2", "PJSIP/trunk-011",
            ChannelState.Ring, linkedId: "rc-linked-6", context: "from-trunk");

        _fixture.SessionManager.GetByLinkedId("rc-linked-6").Should().NotBeNull();
    }

    [Fact]
    public void Reconciler_ShouldMarkTimedOut_WhenRinging()
    {
        _fixture.SimulateNewChannel("rc-ring-1", "PJSIP/100-005",
            ChannelState.Ring, linkedId: "rc-linked-7", context: "from-internal");
        _fixture.SimulateNewChannel("rc-ring-2", "PJSIP/trunk-012",
            ChannelState.Ring, linkedId: "rc-linked-7");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-7")!;

        // Dial then move to Ringing
        _fixture.SimulateDialBegin("rc-ring-1", "rc-ring-2", "PJSIP/trunk-012");
        session.State.Should().Be(CallSessionState.Dialing);

        // Simulate state change to Ringing
        _fixture.SimulateStateChange("rc-ring-2", ChannelState.Ringing);
        session.State.Should().Be(CallSessionState.Ringing);

        // Reconciler can time out ringing sessions
        var result = SessionReconciler.TryMarkTimedOut(session);
        result.Should().BeTrue();
        session.State.Should().Be(CallSessionState.TimedOut);
    }
}
