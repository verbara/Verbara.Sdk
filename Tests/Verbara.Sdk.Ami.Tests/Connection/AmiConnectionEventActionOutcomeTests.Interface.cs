using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// How an event-generating action ended, as a caller that holds only <see cref="IAmiConnection"/> sees it. The SDK's
/// connection reports the outcome (<see cref="IAmiConnection.ReportsEventActionOutcome"/> reads <see langword="true"/>):
/// a refusal sets <see cref="EventActionOutcome.Rejection"/>, and a completed action leaves the outcome at its defaults.
/// A wrapper that forwards the outcome overload and the capability reports the refusal too. A wrapper that forwards
/// neither, and a substitute configured only on the plain overload, read <see langword="false"/>: through the default
/// method a non-forwarding wrapper yields the plain overload's events and leaves the outcome not
/// <see cref="EventActionOutcome.Reported"/>.
/// </summary>
/// <remarks>
/// The outcome's setters are internal to the core package, so no test double can report one: every wrapper here wraps a
/// real <see cref="AmiConnection"/> over a <see cref="PipedSocket"/>.
/// </remarks>
public sealed partial class AmiConnectionEventActionOutcomeTests
{
    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldReportTheRefusalThroughTheInterface_WhenAsteriskRefusesTheAction()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        IAmiConnection through = connection;
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(through.SendEventGeneratingActionAsync(new QueueStatusAction(), outcome));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Error", id, [new("Message", UnknownCommand)])).Should().BeTrue("the peer refuses the action");
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            through.ReportsEventActionOutcome.Should().BeTrue("the SDK's connection reports how an action ended");
            result.Error.Should().BeNull("a refusal still ends the enumeration without an error");
            outcome.Rejection.Should().Be(UnknownCommand, "the refusal reaches a caller through the interface");
            outcome.Reported.Should().BeTrue("the connection that ran the action reports how it ended");
            outcome.SessionEnded.Should().BeFalse();
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldLeaveTheOutcomeAtItsDefaults_WhenAsteriskCompletesTheAction()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        IAmiConnection through = connection;
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(through.SendEventGeneratingActionAsync(new StatusAction(), outcome));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteStatusListAsync(peer, id, complete: true, "1700000000.1")).Should().BeTrue("the peer lists one channel");
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            result.Error.Should().BeNull();
            outcome.Rejection.Should().BeNull("Asterisk did not refuse the action");
            outcome.SessionEnded.Should().BeFalse("the session did not end");
            outcome.Reported.Should().BeTrue("the connection that ran the action reports how it ended");
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldReportTheRefusal_WhenAWrapperForwardsTheOutcome()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        IAmiConnection wrapper = new ForwardingWrapper(connection);
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(wrapper.SendEventGeneratingActionAsync(new QueueStatusAction(), outcome));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await peer.RespondAsync("Error", id, [new("Message", UnknownCommand)])).Should().BeTrue("the peer refuses the action");
        await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            wrapper.ReportsEventActionOutcome.Should().BeTrue("the wrapper forwards the capability");
            outcome.Rejection.Should().Be(UnknownCommand, "the wrapper forwards the outcome overload");
            outcome.Reported.Should().BeTrue("the real connection behind the wrapper reports how the action ended");
        }
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldYieldThePlainEventsAndLeaveTheOutcomeNotReported_WhenAWrapperDoesNotForwardIt()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);
        IAmiConnection wrapper = new PlainWrapper(connection);
        var outcome = new EventActionOutcome();

        var ended = ReadToEndAsync(wrapper.SendEventGeneratingActionAsync(new StatusAction(), outcome));
        var id = await ReadActionIdAsync(peer, peerCts);
        (await WriteStatusListAsync(peer, id, complete: true, "1700000000.1", "1700000000.2")).Should().BeTrue();
        var result = await ended.WaitAsync(Bound);

        using (new AssertionScope())
        {
            wrapper.ReportsEventActionOutcome.Should().BeFalse("a wrapper that does not forward the capability cannot say");
            result.Events.OfType<Verbara.Sdk.Ami.Events.StatusEvent>().Select(status => status.UniqueId)
                .Should().Equal(["1700000000.1", "1700000000.2"], "the default method yields the plain overload's events");
            outcome.Reported.Should().BeFalse("the default method only forwards, and writes nothing on the outcome");
            outcome.Rejection.Should().BeNull("nothing was written on the outcome");
            outcome.SessionEnded.Should().BeFalse("nothing was written on the outcome");
        }
    }

    [Fact]
    public void ReportsEventActionOutcome_ShouldReadFalse_WhenASubstituteIsConfiguredOnlyOnThePlainOverload()
    {
        var substitute = Substitute.For<IAmiConnection>();
        substitute.State.Returns(AmiConnectionState.Connected);
        substitute.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ManagerEvent>());

        substitute.ReportsEventActionOutcome.Should().BeFalse(
            "a mock does not run a default member's body, and what it answers for an unconfigured bool is the capability's default");
    }

    /// <summary>Forwards every member, the outcome overload and the capability included.</summary>
    private sealed class ForwardingWrapper(AmiConnection inner) : PlainWrapper(inner), IAmiConnection
    {
        public bool ReportsEventActionOutcome => Inner.ReportsEventActionOutcome;

        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action, EventActionOutcome? outcome,
            CancellationToken cancellationToken = default) =>
            Inner.SendEventGeneratingActionAsync(action, outcome, cancellationToken);
    }

    /// <summary>Forwards every member the interface had before the outcome overload, and nothing else.</summary>
    private class PlainWrapper(AmiConnection inner) : IAmiConnection
    {
        protected AmiConnection Inner { get; } = inner;

        public AmiConnectionState State => Inner.State;
        public string? AsteriskVersion => Inner.AsteriskVersion;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => Inner.ConnectAsync(cancellationToken);
        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default) =>
            Inner.SendActionAsync(action, cancellationToken);
        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
            where TResponse : ManagerResponse => Inner.SendActionAsync<TResponse>(action, cancellationToken);
        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => Inner.SendEventGeneratingActionAsync(action, cancellationToken);
        public IDisposable Subscribe(IObserver<ManagerEvent> observer) => Inner.Subscribe(observer);
        public event Func<ManagerEvent, ValueTask>? OnEvent { add => Inner.OnEvent += value; remove => Inner.OnEvent -= value; }
        public event Action? Reconnected { add => Inner.Reconnected += value; remove => Inner.Reconnected -= value; }
        public event Action<AmiConnectionStateChange>? StateChanged { add => Inner.StateChanged += value; remove => Inner.StateChanged -= value; }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => Inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
