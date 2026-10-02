using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Binds the session-side half of "a leg is one participant": a leg reported as added while it is
/// already a present participant of its call adds nothing, and a leg's departure marks the
/// participant with that channel id that has not left yet — never one that already left.
///
/// <para>The channel table admits a channel once per server, so these orderings reach the manager by
/// another route: one manager attached to two servers whose channel tables both announce the same
/// channel. Every step is driven synchronously on the test's thread; the assertions are counts.</para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerOneLegOnceTests : IAsyncLifetime
{
    private const string LinkedId = "L-1";
    private const string CallerUid = "c-1";
    private const string AgentUid = "a-1";

    private readonly IAmiConnection _firstConnection = Substitute.For<IAmiConnection>();
    private readonly IAmiConnection _secondConnection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _first;
    private readonly VerbaraServer _second;
    private readonly CallSessionManager _sut;
    private readonly List<SessionDomainEvent> _events = [];
    private readonly IDisposable _subscription;

    public CallSessionManagerOneLegOnceTests()
    {
        _firstConnection.AsteriskVersion.Returns("21.0.0");
        _secondConnection.AsteriskVersion.Returns("21.0.0");
        _first = new VerbaraServer(_firstConnection, NullLogger<VerbaraServer>.Instance);
        _second = new VerbaraServer(_secondConnection, NullLogger<VerbaraServer>.Instance);
        _sut = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
        _sut.AttachToServer(_first, "srv-1");
        _sut.AttachToServer(_second, "srv-2");
        _subscription = _sut.Events.Subscribe(_events.Add);
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _subscription.Dispose();
        await _sut.DisposeAsync();
        await _second.DisposeAsync();
        await _first.DisposeAsync();
    }

    private static void Caller(VerbaraServer server) =>
        server.Channels.OnNewChannel(CallerUid, "PJSIP/trunk-001", ChannelState.Up,
            context: "from-trunk", linkedId: LinkedId);

    private static void Agent(VerbaraServer server) =>
        server.Channels.OnNewChannel(AgentUid, "PJSIP/100-001", ChannelState.Up, linkedId: LinkedId);

    private int EndingsOf(CallSession call) =>
        _events.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId);

    [Fact]
    public void OnChannelAdded_ShouldHoldTheLegOnceAndEndTheCallOnItsHangups_WhenTwoServersAnnounceTheSameChannel()
    {
        Caller(_first);
        Agent(_first);
        Caller(_second);
        var call = _sut.GetByLinkedId(LinkedId)
            ?? throw new InvalidOperationException("premise: a session was opened for the call");
        var participantsAfterAdds = call.Participants.Count;

        _first.Channels.OnHangup(CallerUid, HangupCause.NormalClearing);
        _second.Channels.OnHangup(CallerUid, HangupCause.NormalClearing);
        _first.Channels.OnHangup(AgentUid, HangupCause.NormalClearing);

        new { Participants = participantsAfterAdds, Ended = EndingsOf(call) }.Should().BeEquivalentTo(
            new { Participants = 2, Ended = 1 },
            "a leg reported while it is already a present participant is the same leg, so the call holds it "
            + "once and ends when its two legs have hung up");
    }

    [Fact]
    public void OnChannelRemoved_ShouldMarkTheLegStillPresentAndKeepTheFirstDeparture_WhenALegWithTheSameIdAlreadyLeft()
    {
        Caller(_first);
        Agent(_first);
        var call = _sut.GetByLinkedId(LinkedId)
            ?? throw new InvalidOperationException("premise: a session was opened for the call");
        _first.Channels.OnHangup(CallerUid, HangupCause.UserBusy);
        var departed = call.Participants.Single(p => p.UniqueId == CallerUid);
        var firstLeftAt = departed.LeftAt;

        Caller(_second);
        _second.Channels.OnHangup(CallerUid, HangupCause.NormalClearing);
        _first.Channels.OnHangup(AgentUid, HangupCause.NormalClearing);

        new
        {
            Ended = EndingsOf(call),
            FirstLeftAt = departed.LeftAt,
            FirstCause = departed.HangupCause,
            StillPresent = call.Participants.Count(p => !p.LeftAt.HasValue),
        }.Should().BeEquivalentTo(
            new { Ended = 1, FirstLeftAt = firstLeftAt, FirstCause = (HangupCause?)HangupCause.UserBusy, StillPresent = 0 },
            "a departure marks the participant with that id that has not left; the one that already left keeps "
            + "its own departure, and with every leg gone the call ends");
    }
}
