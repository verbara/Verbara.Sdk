using Verbara.Sdk.VoiceAi.AudioSocket;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Holds the two properties of <see cref="ParkingSessionHandler"/> that the broker's lifetime tests
/// rely on, so a broken double fails here rather than turning an assertion about the broker vacuous.
/// </summary>
/// <remarks>
/// The handler is started the way the broker's dispatcher starts it: inline, from a delegate
/// subscribed to <see cref="AudioSocketServer.OnSessionStarted"/> ahead of a
/// <see cref="SessionStartedProbe"/>. No broker is involved, so nothing here asserts about the
/// broker. Real server, real TCP on <c>127.0.0.1</c> port 0 (ADR-0044). Every wait is bounded and
/// ends on the signal it asserts.
/// </remarks>
public sealed class ParkingSessionHandlerTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HandleSessionAsync_ShouldRecordTheCallAndItsTokenBeforeALaterSubscriberRuns_WhenStartedInline()
    {
        await using var server = LoopbackServer();
        var handler = new ParkingSessionHandler();
        using var handlerTokenSource = new CancellationTokenSource();
        var handlerToken = handlerTokenSource.Token;
        server.OnSessionStarted += session => StartInline(handler, session, handlerToken);
        var probe = SessionStartedProbe.SubscribeTo(server);
        await server.StartAsync(CancellationToken.None);
        var channel = Guid.NewGuid();
        await using var peer = await AudioSocketPeer.ConnectAsync(server, channel);

        await probe.Started(channel).WaitAsync(SignalTimeout);

        handler.Calls.Should().ContainSingle(
            "the call is recorded on entry, before the handler's first await, so it is there by the time a later subscriber runs");
        var call = handler.Calls.Single();
        call.ChannelId.Should().Be(channel);
        call.Token.Should().Be(handlerToken);
        call.Ended.IsCompleted.Should().BeFalse("the handler is parked until its token or a release ends it");

        handler.Release();
        (await call.Ended.WaitAsync(SignalTimeout)).Should().Be(ParkedCallEnding.Released);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEndThePark_WhenItsTokenIsCancelled()
    {
        await using var server = LoopbackServer();
        var handler = new ParkingSessionHandler();
        using var handlerTokenSource = new CancellationTokenSource();
        var handlerToken = handlerTokenSource.Token;
        server.OnSessionStarted += session => StartInline(handler, session, handlerToken);
        await server.StartAsync(CancellationToken.None);
        await using var peer = await AudioSocketPeer.ConnectAsync(server, Guid.NewGuid());
        var call = await handler.FirstCall.WaitAsync(SignalTimeout);

        await handlerTokenSource.CancelAsync();

        (await call.Ended.WaitAsync(SignalTimeout)).Should().Be(
            ParkedCallEnding.Cancelled, "the park ends on the token the handler was handed, with no release");
    }

    private static AudioSocketServer LoopbackServer() =>
        new(new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 }, NullLogger<AudioSocketServer>.Instance);

    /// <summary>Starts the handler inline and lets it run on, as the broker's dispatcher does.</summary>
    private static ValueTask StartInline(ParkingSessionHandler handler, AudioSocketSession session, CancellationToken token)
    {
        _ = handler.HandleSessionAsync(session, token).AsTask();
        return ValueTask.CompletedTask;
    }
}
