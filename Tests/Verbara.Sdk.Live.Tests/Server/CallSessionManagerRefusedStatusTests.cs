using System.Collections.Concurrent;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// What a refused <c>Status</c> did one layer up: the reload removed every held channel, and the session manager,
/// attached to the server, ended the call those channels made while Asterisk still held it — one
/// <see cref="CallEndedEvent"/> for a call nobody hung up (dossier L12).
/// </summary>
/// <remarks>
/// <para>
/// The test lives in <c>Verbara.Sdk.Live.Tests</c>, not in <c>Verbara.Sdk.Sessions.Tests</c>: it drives a real
/// <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and server through <see cref="Run"/>, whose clock seam is
/// internal to <c>Verbara.Sdk.Live</c>, and no shipped package grants <c>InternalsVisibleTo</c> to
/// <c>Sessions.Tests</c> for it (the owner's ruling, 2026-09-30). The manager is built with its public constructor
/// over a test-local <see cref="SessionStoreBase"/>, since the default in-memory store is internal to Sessions.
/// </para>
/// <para>
/// The manager ends a call inside the channel table's <c>ChannelRemoved</c>, raised inline by the reconciliation, and
/// the reload asks <c>Agents</c> only after it has reconciled: the test waits until the second peer has read that
/// <c>Agents</c> before it counts, so "no call ended" cannot be read before the reload has run.
/// </para>
/// </remarks>
public sealed class CallSessionManagerRefusedStatusTests
{
    private const string ServerId = "srv-1";

    private static IReadOnlyList<StatusChannel> OneCallTwoLegs() =>
    [
        new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
        new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.1"),
    ];

    [Fact]
    public async Task CallSessionManager_ShouldNotEndTheCall_WhenAReloadsStatusIsRefused()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs(), StatusRefusedFromAsk = 1 };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await using var manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new TestStore());
        var events = new DomainEvents();
        using var subscription = manager.Events.Subscribe(events);
        manager.AttachToServer(run.Server, ServerId);
        await run.StartServerAsync();
        var startedAtStart = events.Count<CallStartedEvent>();

        run.ReleaseSecondPeer();
        first.CloseSession();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        // The barrier: the reload asks Agents only after it has read Status and reconciled.
        var pastTheReconcile = await CompletesWithinBoundAsync(second.AskedAtLeast("Agents", 1));

        using (new AssertionScope())
        {
            startedAtStart.Should().Be(1, "the start loaded both legs of one call, and the manager opened that call");
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            pastTheReconcile.Should().BeTrue("the reload got past its Status and its reconciliation");
            second.Asked("Status").Should().Be(1, "the reload asked Status once and Asterisk refused it");
            events.Count<CallEndedEvent>().Should().Be(0,
                "Asterisk refused the reload's Status, which is no evidence that the call ended");
            run.Server.Channels.ChannelCount.Should().Be(2, "both legs Asterisk still holds are still held");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>The manager's domain events, kept in the order it raised them.</summary>
    private sealed class DomainEvents : IObserver<SessionDomainEvent>
    {
        private readonly ConcurrentQueue<SessionDomainEvent> _events = new();

        public int Count<T>() where T : SessionDomainEvent => _events.OfType<T>().Count();

        public void OnNext(SessionDomainEvent value) => _events.Enqueue(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    /// <summary>A store that keeps the last save of each call; the test reads the manager's events, not the store.</summary>
    private sealed class TestStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, CallSession> _sessions = new(StringComparer.Ordinal);

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _sessions[session.SessionId] = session;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId));
    }
}
