using System.Collections.Concurrent;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Pins the dispatch ADR-0013 is for: a started <see cref="VoiceAiSessionBroker"/> hands every session
/// the AudioSocket server accepts to the registered <see cref="ISessionHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Before this test the broker ran in no suite: the DI tests resolve it and never start it, and the
/// tests that need a live session subscribe to <see cref="AudioSocketServer.OnSessionStarted"/>
/// themselves and call the handler directly (ADR-0013 addendum, 2026-09-26). So nothing failed if the
/// broker stopped dispatching, and the seam's one promise — pick a handler by DI registration, and the
/// acceptor reaches it — was held by nothing.
/// </para>
/// <para>
/// Real server, real TCP, real <see cref="AudioSocketClient"/>: the broker is reached only through the
/// server's event, which nothing outside the server can raise. Ordered by construction — both hosted
/// services are started before either caller connects — and the one bounded wait ends on the signal it
/// asserts: the handler having seen both channels.
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
