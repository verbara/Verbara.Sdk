using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Verbara.Sdk.Sessions.FunctionalTests;

public sealed class IndexAndQueryTests : IAsyncLifetime
{
    /// <summary>The manager's release clock: it moves only when a test moves it.</summary>
    private readonly ManualClock _releaseClock = new(DateTimeOffset.UtcNow);
    private readonly SessionTestFixture _fixture;

    public IndexAndQueryTests() => _fixture = new SessionTestFixture(new SessionOptions(), _releaseClock);

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void GetByChannelId_ShouldReturnCorrectSession()
    {
        _fixture.SimulateNewChannel("iq-ch-1", "PJSIP/trunk-001",
            ChannelState.Ring, linkedId: "iq-linked-1", context: "from-trunk");
        _fixture.SimulateNewChannel("iq-ch-2", "PJSIP/100-001",
            ChannelState.Ring, linkedId: "iq-linked-1");

        // Second independent call
        _fixture.SimulateNewChannel("iq-ch-3", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: "iq-linked-2", context: "from-trunk");

        var session1 = _fixture.SessionManager.GetByChannelId("iq-ch-1");
        var session2 = _fixture.SessionManager.GetByChannelId("iq-ch-2");
        var session3 = _fixture.SessionManager.GetByChannelId("iq-ch-3");

        session1.Should().NotBeNull();
        session2.Should().NotBeNull();
        session3.Should().NotBeNull();
        session1.Should().BeSameAs(session2);
        session1.Should().NotBeSameAs(session3);
    }

    [Fact]
    public void GetByLinkedId_ShouldReturnCorrectSession()
    {
        _fixture.SimulateNewChannel("iq-lk-1", "PJSIP/trunk-003",
            ChannelState.Ring, linkedId: "iq-linked-3", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("iq-linked-3");
        session.Should().NotBeNull();
        session!.LinkedId.Should().Be("iq-linked-3");

        // Non-existent linkedId
        _fixture.SessionManager.GetByLinkedId("nonexistent").Should().BeNull();
    }

    [Fact]
    public void ActiveSessions_ShouldExcludeCompletedAndFailed()
    {
        // Active session
        _fixture.SimulateNewChannel("iq-act-1", "PJSIP/trunk-004",
            ChannelState.Ring, linkedId: "iq-linked-4", context: "from-trunk");

        // Completed session
        _fixture.SimulateNewChannel("iq-cmp-1", "PJSIP/trunk-005",
            ChannelState.Ring, linkedId: "iq-linked-5", context: "from-trunk");
        _fixture.SimulateNewChannel("iq-cmp-2", "PJSIP/100-005",
            ChannelState.Ring, linkedId: "iq-linked-5");
        _fixture.SimulateAnswer("iq-cmp-2");
        _fixture.SimulateHangup("iq-cmp-2");
        _fixture.SimulateHangup("iq-cmp-1");

        var completed = _fixture.SessionManager.GetByLinkedId("iq-linked-5")!;
        completed.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);

        var active = _fixture.SessionManager.ActiveSessions.ToList();
        active.Should().Contain(s => s.LinkedId == "iq-linked-4");
        active.Should().NotContain(s => s.LinkedId == "iq-linked-5");
    }

    /// <summary>
    /// <see cref="CallSessionManager.GetRecentCompleted"/> returns exactly the ended calls the manager
    /// still holds — no call in progress, none past retention — most recent first. Three calls end; the
    /// release clock is moved until they ended one tick more than
    /// <see cref="SessionOptions.CompletedRetention"/> ago; three more end. A list that kept a released
    /// call, or grew past what retention holds, shows it as an id that should not be there.
    /// </summary>
    [Fact]
    public void GetRecentCompleted_ShouldReturnExactlyTheEndedCallsStillHeld_WhenEarlierOnesEndedMoreThanTheRetentionPeriodAgo()
    {
        var live = OpenCall("iq-rc-live");
        var first = EndCalls("iq-rc-first", 3);
        var beforeRetention = _fixture.SessionManager.GetRecentCompleted(10).ToList();

        // Every call that ends from here on ends within retention; the first three do not.
        var cutoff = first.Max(s => s.CompletedAt!.Value) + TimeSpan.FromTicks(1);
        _releaseClock.Advance(cutoff + _fixture.Options.CompletedRetention - _releaseClock.GetUtcNow());
        var second = EndCalls("iq-rc-second", 3);
        second.Should().OnlyContain(s => s.CompletedAt >= cutoff, "premise: the second three ended within retention");
        var afterRetention = _fixture.SessionManager.GetRecentCompleted(10).ToList();

        new
        {
            BeforeRetention = beforeRetention.Select(s => s.SessionId),
            AfterRetention = afterRetention.Select(s => s.SessionId),
            LiveListed = beforeRetention.Concat(afterRetention).Contains(live),
        }.Should().BeEquivalentTo(
            new
            {
                BeforeRetention = first.Select(s => s.SessionId),
                AfterRetention = second.Select(s => s.SessionId),
                LiveListed = false,
            },
            "the recent completed calls are exactly the ended calls the manager still holds: the first three "
            + "while they are within retention, and only the second three once the first ended more than "
            + "the retention period ago — never the call still in progress");
        beforeRetention.Should().BeInDescendingOrder(s => s.CompletedAt, "the most recent ending comes first");
        afterRetention.Should().BeInDescendingOrder(s => s.CompletedAt, "the most recent ending comes first");
    }

    [Fact]
    public void GetByBridgeId_ShouldReturnSession_WhenBridgeAssociated()
    {
        var (_, _, _) = _fixture.SimulateInboundCallAnswered(
            callerUid: "iq-br-caller", agentUid: "iq-br-agent",
            linkedId: "iq-linked-br", bridgeId: "iq-br-001");

        var session = _fixture.SessionManager.GetByBridgeId("iq-br-001");
        session.Should().NotBeNull();
        session!.BridgeId.Should().Be("iq-br-001");

        // Non-existent bridge
        _fixture.SessionManager.GetByBridgeId("nonexistent-bridge").Should().BeNull();
    }

    /// <summary>One leg of a new call, which stays in progress.</summary>
    private CallSession OpenCall(string linkedId)
    {
        _fixture.SimulateNewChannel($"{linkedId}-caller", $"PJSIP/trunk-{linkedId}",
            ChannelState.Ring, linkedId: linkedId, context: "from-trunk");
        return _fixture.SessionManager.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"premise: a call was opened for '{linkedId}'");
    }

    /// <summary><paramref name="count"/> answered calls, each ended — both legs hung up — before the next starts.</summary>
    private List<CallSession> EndCalls(string prefix, int count)
    {
        var ended = new List<CallSession>(count);
        for (var i = 0; i < count; i++)
        {
            var linked = $"{prefix}-{i}";
            var call = OpenCall(linked);
            _fixture.SimulateNewChannel($"{linked}-agent", $"PJSIP/100-{linked}", ChannelState.Ring, linkedId: linked);
            _fixture.SimulateAnswer($"{linked}-agent");
            _fixture.SimulateHangup($"{linked}-agent");
            _fixture.SimulateHangup($"{linked}-caller");
            call.State.Should().BeOneOf([CallSessionState.Completed, CallSessionState.Failed], $"premise: '{linked}' ended");
            ended.Add(call);
        }

        return ended;
    }
}
