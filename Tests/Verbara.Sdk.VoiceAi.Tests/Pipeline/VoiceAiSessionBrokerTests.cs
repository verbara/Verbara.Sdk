using System.Collections.Concurrent;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Verbara.Sdk.VoiceAi.DependencyInjection;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// The broker's two jobs. It hands every session the AudioSocket server accepts to the registered
/// <see cref="ISessionHandler"/> (ADR-0013), and it owns the token those handlers run under, which
/// its stop phase and its teardown end (ADR-0059).
/// </summary>
/// <remarks>
/// <para>
/// Before the dispatch pin the broker ran in no suite: the DI tests resolve it and never start it, and
/// the tests that need a live session subscribe to <see cref="AudioSocketServer.OnSessionStarted"/>
/// themselves and call the handler directly (ADR-0013 addendum, 2026-09-26). So nothing failed if the
/// broker stopped dispatching, and the seam's one promise — pick a handler by DI registration, and the
/// acceptor reaches it — was held by nothing.
/// </para>
/// <para>
/// The lifetime tests hold the handler token to its two readings. The token passed to
/// <see cref="VoiceAiSessionBroker.StartAsync"/> means only that the start was aborted, so it never
/// reaches a handler. What cancels a handler's token is the host withdrawing its grace (the token
/// passed to the stop) or the broker being disposed, never a stop within its budget. Once stopped or
/// disposed, the broker hands no new session on, and it hands each session on once however many
/// times it was started.
/// </para>
/// <para>
/// Real server, real TCP, real <see cref="AudioSocketClient"/> on <c>127.0.0.1</c> port 0 (ADR-0044):
/// the broker is reached only through the server's event, which nothing outside the server can raise.
/// Ordered by construction, never by a delay (design D8). A handler call is observed through
/// <see cref="ParkingSessionHandler"/>, which records it on entry, and "not handed on" is read only
/// after a <see cref="SessionStartedProbe"/> subscribed after the broker has fired for that session.
/// Every bounded wait ends on the signal it asserts.
/// </para>
/// </remarks>
public sealed class VoiceAiSessionBrokerTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StartAsync_ShouldHandEachAcceptedSessionToTheHandler_WhenTheServerAcceptsThem()
    {
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        var handler = new RecordingSessionHandler(expectedSessions: 2);
        var broker = new VoiceAiSessionBroker(server, handler, NullLogger<VoiceAiSessionBroker>.Instance);

        await server.StartAsync(CancellationToken.None);
        await broker.StartAsync(CancellationToken.None);

        var firstChannel = Guid.NewGuid();
        var secondChannel = Guid.NewGuid();
        await using var firstCaller = new AudioSocketClient("127.0.0.1", server.BoundPort, firstChannel);
        await using var secondCaller = new AudioSocketClient("127.0.0.1", server.BoundPort, secondChannel);
        await firstCaller.ConnectAsync(CancellationToken.None);
        await secondCaller.ConnectAsync(CancellationToken.None);

        var handled = await handler.AllHandled.WaitAsync(SignalTimeout);

        handled.Should().BeEquivalentTo(
            [firstChannel, secondChannel],
            "the broker dispatches each accepted session, by its channel, to the registered handler");

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldNotEndTheHandler_WhenTheStartTokenIsCancelledAfterTheStartCompleted()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        using var start = new CancellationTokenSource();
        await rig.Broker.StartAsync(start.Token);
        var call = await rig.HandOneSessionOnAsync();

        // The host releases the source behind its start token once the start returns. Cancelling it
        // here is the only thing an aborted start could still do to a token kept from it.
        await start.CancelAsync();

        call.Token.IsCancellationRequested.Should().BeFalse(
            "the start token only means the start was aborted, so the broker hands its handlers a " +
            "token of its own rather than that one");
        call.Ended.IsCompleted.Should().BeFalse("nothing that ends a handler has happened");
    }

    [Fact]
    public async Task StopAsync_ShouldLeaveTheHandlerTokenUncancelled_WhenTheStopIsGraceful()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        await rig.Broker.StartAsync(CancellationToken.None);
        var call = await rig.HandOneSessionOnAsync();
        using var grace = new CancellationTokenSource();

        await rig.Broker.StopAsync(grace.Token);

        call.Token.IsCancellationRequested.Should().BeFalse(
            "a stop within its budget does not cut a session short: the server's own stop, which a " +
            "host runs after the broker's, ends the sessions a graceful shutdown ends");
        call.Ended.IsCompleted.Should().BeFalse("the handler is still running its session");
    }

    [Fact]
    public async Task StopAsync_ShouldCancelTheHandlerToken_WhenTheStopTokenIsAlreadyCancelled()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        await rig.Broker.StartAsync(CancellationToken.None);
        var call = await rig.HandOneSessionOnAsync();

        // A host whose shutdown is no longer graceful hands its stop a token that is already cancelled.
        await rig.Broker.StopAsync(new CancellationToken(canceled: true));

        call.Token.IsCancellationRequested.Should().BeTrue(
            "a stop whose token is already cancelled is not graceful, so it ends the running handlers");
        (await call.Ended.WaitAsync(SignalTimeout)).Should().Be(
            ParkedCallEnding.Cancelled, "the handler's token is what ended it");
    }

    [Fact]
    public async Task StopAsync_ShouldCancelTheHandlerToken_WhenTheStopTokenIsCancelledAfterTheStopReturned()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        await rig.Broker.StartAsync(CancellationToken.None);
        var call = await rig.HandOneSessionOnAsync();
        using var grace = new CancellationTokenSource();
        await rig.Broker.StopAsync(grace.Token);
        call.Token.IsCancellationRequested.Should().BeFalse(
            "the stop returned within its budget, so the handler still runs");

        // The host's shutdown budget runs out after the broker's stop has returned.
        await grace.CancelAsync();

        call.Token.IsCancellationRequested.Should().BeTrue(
            "the host withdrawing its grace after the stop returned still ends the running handlers");
        (await call.Ended.WaitAsync(SignalTimeout)).Should().Be(
            ParkedCallEnding.Cancelled, "the handler's token is what ended it");
    }

    [Fact]
    public async Task StopAsync_ShouldNotHandASessionOn_WhenItArrivesAfterTheStop()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        await rig.Broker.StartAsync(CancellationToken.None);
        await rig.Broker.StopAsync(CancellationToken.None);
        var probe = SessionStartedProbe.SubscribeTo(rig.Server);

        var channel = await rig.ConnectPeerAsync();
        await probe.Started(channel).WaitAsync(SignalTimeout);

        rig.Handler.Calls.Should().BeEmpty(
            "a stopped broker hands no new session on, and the probe, subscribed after the broker, " +
            "fires only once the broker's dispatch for the same session has run or never will");
        rig.Server.ActiveSessionCount.Should().Be(
            1, "a session the broker does not hand on stays with the server that accepted it");
    }

    [Fact]
    public async Task StartAsync_ShouldHandEachSessionOnOnce_WhenCalledTwice()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        await rig.Broker.StartAsync(CancellationToken.None);
        await rig.Broker.StartAsync(CancellationToken.None);
        var probe = SessionStartedProbe.SubscribeTo(rig.Server);

        var channel = await rig.ConnectPeerAsync();
        await probe.Started(channel).WaitAsync(SignalTimeout);

        rig.Handler.Calls.Should().ContainSingle(
            "a second start does not subscribe the broker again, so the session reaches the handler once");
    }

    [Fact]
    public async Task ServiceProviderDisposeAsync_ShouldCancelAHandlerThatOutlivedAGracefulStop_WhenRegisteredByAddVoiceAiPipeline()
    {
        var handler = new ParkingSessionHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        // Registered before AddVoiceAiPipeline, whose TryAdd then keeps it: this is the handler the
        // broker runs.
        services.AddSingleton<ISessionHandler>(handler);
        services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
        services.AddVoiceAiPipeline<NoopConversationHandler>();
        var provider = services.BuildServiceProvider();
        try
        {
            var hosted = provider.GetServices<IHostedService>().ToList();
            hosted.Should().Contain(
                provider.GetRequiredService<VoiceAiSessionBroker>(),
                "the hosted-service registration forwards the singleton, which is why the container " +
                "tracks the one broker under two registrations");
            foreach (var service in hosted)
                await service.StartAsync(CancellationToken.None);
            var server = provider.GetRequiredService<AudioSocketServer>();
            await using var peer = await AudioSocketPeer.ConnectAsync(server, Guid.NewGuid());
            var call = await handler.FirstCall.WaitAsync(SignalTimeout);

            // A graceful stop in the host's order: the broker, then the server, whose stop ends the
            // session. The parked handler ignores its session ending, like a handler stuck in a
            // provider call, so only its token can end it.
            foreach (var service in Enumerable.Reverse(hosted))
                await service.StopAsync(CancellationToken.None);
            call.Token.IsCancellationRequested.Should().BeFalse("a graceful stop does not cancel the handler");

            var dispose = async () => await provider.DisposeAsync();

            await dispose.Should().NotThrowAsync(
                "the container disposes the broker once per registration, and every disposal after " +
                "the first is ignored");
            call.Token.IsCancellationRequested.Should().BeTrue(
                "teardown cancels the broker's token before releasing it, so a handler that outlived " +
                "the stop is not left under a token nothing can cancel");
            (await call.Ended.WaitAsync(SignalTimeout)).Should().Be(
                ParkedCallEnding.Cancelled, "the handler's token is what ended it");
        }
        finally
        {
            handler.Release();
        }
    }

    [Fact]
    public async Task Dispose_ShouldNotThrowAndKeepTheTokenCancelled_WhenCalledASecondTime()
    {
        // Built without BrokerRig, whose teardown disposes the broker once more: a teardown that threw
        // would replace this test's own assertion as the reported failure.
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        var handler = new ParkingSessionHandler();
        var broker = new VoiceAiSessionBroker(server, handler, NullLogger<VoiceAiSessionBroker>.Instance);
        await broker.StartAsync(CancellationToken.None);
        await using var peer = await AudioSocketPeer.ConnectAsync(server, Guid.NewGuid());
        var call = await handler.FirstCall.WaitAsync(SignalTimeout);
        broker.Dispose();

        var second = () => broker.Dispose();

        second.Should().NotThrow(
            "IDisposable requires every call after the first to be ignored, and the SDK's own " +
            "registration makes the container dispose the broker twice");
        call.Token.IsCancellationRequested.Should().BeTrue(
            "the first disposal cancelled the token before releasing its source, and a token read " +
            "before the release keeps reading cancelled");
        (await call.Ended.WaitAsync(SignalTimeout)).Should().Be(
            ParkedCallEnding.Cancelled, "the handler's token is what ended it");
    }

    [Fact]
    public async Task Dispose_ShouldNotHandASessionOn_WhenItArrivesAfterDisposalWithoutAStop()
    {
        await using var rig = await BrokerRig.StartServerAsync();
        await rig.Broker.StartAsync(CancellationToken.None);
        rig.Broker.Dispose();
        var probe = SessionStartedProbe.SubscribeTo(rig.Server);

        var channel = await rig.ConnectPeerAsync();
        await probe.Started(channel).WaitAsync(SignalTimeout);

        rig.Handler.Calls.Should().BeEmpty(
            "a disposed broker hands no new session on even when no stop came first, and the probe, " +
            "subscribed after the broker, fires only once the broker's dispatch has run or never will");
    }

    /// <summary>
    /// A started server on <c>127.0.0.1</c> port 0 with a broker over it that the test starts itself,
    /// and a <see cref="ParkingSessionHandler"/> behind the broker. Disposing it ends every park, then
    /// disposes the broker, the peers and the server. The broker goes before the server, as it does
    /// in a host's container.
    /// </summary>
    private sealed class BrokerRig : IAsyncDisposable
    {
        private readonly List<AudioSocketClient> _peers = [];

        private BrokerRig(AudioSocketServer server)
        {
            Server = server;
            Broker = new VoiceAiSessionBroker(server, Handler, NullLogger<VoiceAiSessionBroker>.Instance);
        }

        public AudioSocketServer Server { get; }

        public ParkingSessionHandler Handler { get; } = new();

        public VoiceAiSessionBroker Broker { get; }

        public static async Task<BrokerRig> StartServerAsync()
        {
            var server = new AudioSocketServer(
                new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
                NullLogger<AudioSocketServer>.Instance);
            await server.StartAsync(CancellationToken.None);
            return new BrokerRig(server);
        }

        /// <summary>Connects a peer for a new channel, which it identifies, and returns that channel.</summary>
        public async Task<Guid> ConnectPeerAsync()
        {
            var channel = Guid.NewGuid();
            _peers.Add(await AudioSocketPeer.ConnectAsync(Server, channel));
            return channel;
        }

        /// <summary>
        /// Connects a peer and returns the handler's call for its session, once the broker has made it.
        /// Call it after starting the broker.
        /// </summary>
        public async Task<ParkedCall> HandOneSessionOnAsync()
        {
            var channel = await ConnectPeerAsync();
            var call = await Handler.FirstCall.WaitAsync(SignalTimeout);
            call.ChannelId.Should().Be(channel, "the broker hands on the session of the peer that connected");
            return call;
        }

        public async ValueTask DisposeAsync()
        {
            Handler.Release();
            Broker.Dispose();
            foreach (var peer in _peers)
                await peer.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}

/// <summary>Records the channel of every session it is handed and completes once it has seen enough.</summary>
file sealed class RecordingSessionHandler(int expectedSessions) : ISessionHandler
{
    private readonly ConcurrentQueue<Guid> _handled = new();
    private readonly TaskCompletionSource<Guid[]> _allHandled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Guid[]> AllHandled => _allHandled.Task;

    public ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
    {
        _handled.Enqueue(session.ChannelId);
        if (_handled.Count >= expectedSessions)
            _allHandled.TrySetResult([.. _handled]);

        return ValueTask.CompletedTask;
    }
}

/// <summary>The conversation handler <c>AddVoiceAiPipeline</c> needs a type for. No session reaches it.</summary>
file sealed class NoopConversationHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct = default) =>
        ValueTask.FromResult(string.Empty);
}
