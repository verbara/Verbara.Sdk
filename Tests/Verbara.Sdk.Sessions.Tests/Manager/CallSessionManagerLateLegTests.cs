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
/// Binds what happens to a leg that arrives after its call has ended, carrying that call's
/// <c>linkedid</c> while the ended call is still retained: it opens a call of its own. An ended call's
/// state is terminal, so a leg joined to it could never have its own ending reported.
///
/// <para>Every step is driven synchronously on the test's thread through one server's channel table;
/// the assertions are counts.</para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerLateLegTests : IAsyncLifetime
{
    private const string LinkedId = "L-late";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sut;
    private readonly List<SessionDomainEvent> _events = [];
    private readonly IDisposable _subscription;

    public CallSessionManagerLateLegTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
        _sut.AttachToServer(_server, "srv-1");
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
        await _server.DisposeAsync();
    }

    [Fact]
    public void OnChannelAdded_ShouldOpenANewCallAndEndItOnTheLegsHangup_WhenTheLegArrivesAfterItsCallEnded()
    {
        var ended = EndedCall();
        var endedParticipants = ended.Participants.Count;
        var eventsBeforeTheLateLeg = _events.Count;

        _server.Channels.OnNewChannel("late-1", "PJSIP/300-late-1", ChannelState.Ring, linkedId: LinkedId);
        var lateCall = _sut.GetByLinkedId(LinkedId);
        var lateLegsCall = _sut.GetByChannelId("late-1");
        _server.Channels.OnHangup("late-1", HangupCause.NormalClearing);

        var sinceTheLateLeg = _events.Skip(eventsBeforeTheLateLeg).ToList();
        new
        {
            SessionsForTheLinkedId = new HashSet<string?>(StringComparer.Ordinal)
                { ended.SessionId, lateCall?.SessionId, lateLegsCall?.SessionId }.Count,
            CallsStarted = sinceTheLateLeg.OfType<CallStartedEvent>().Count(),
            LinkedIdResolvesToTheNewCall = lateCall is not null && !ReferenceEquals(lateCall, ended)
                && ReferenceEquals(lateCall, lateLegsCall),
            EndedCallParticipants = ended.Participants.Count,
            EndedCallState = ended.State,
            CallsEndedAfterTheLateLeg = sinceTheLateLeg.OfType<CallEndedEvent>()
                .Select(e => e.SessionId == ended.SessionId ? "the ended call" : "another call").ToList(),
        }.Should().BeEquivalentTo(
            new
            {
                SessionsForTheLinkedId = 2,
                CallsStarted = 1,
                LinkedIdResolvesToTheNewCall = true,
                EndedCallParticipants = endedParticipants,
                EndedCallState = CallSessionState.Completed,
                CallsEndedAfterTheLateLeg = new List<string> { "another call" },
            },
            "a call that has ended cannot report another leg's ending, so a leg that arrives after it carrying "
            + "its linkedid opens a new call: the ended call keeps its participants and its one ending, the "
            + "linkedid resolves to the live call, and the leg's hangup ends that call once");
    }

    /// <summary>Two legs sharing <see cref="LinkedId"/>, dialled, answered and both hung up: one ended call.</summary>
    private CallSession EndedCall()
    {
        _server.Channels.OnNewChannel("c-1", "PJSIP/trunk-c-1", ChannelState.Ring,
            context: "from-trunk", linkedId: LinkedId);
        _server.Channels.OnNewChannel("a-1", "PJSIP/100-a-1", ChannelState.Ring, linkedId: LinkedId);
        _server.Channels.OnDialBegin("c-1", "a-1", "PJSIP/100-a-1", null);
        _server.Channels.OnNewState("a-1", ChannelState.Up);
        var session = _sut.GetByLinkedId(LinkedId)
            ?? throw new InvalidOperationException("premise: a session was opened for the call");

        _server.Channels.OnHangup("a-1", HangupCause.NormalClearing);
        _server.Channels.OnHangup("c-1", HangupCause.NormalClearing);
        session.State.Should().Be(CallSessionState.Completed, "premise: the call ended with both legs hung up");
        _events.OfType<CallEndedEvent>().Count(e => e.SessionId == session.SessionId)
            .Should().Be(1, "premise: the call's ending was reported once");
        return session;
    }
}
