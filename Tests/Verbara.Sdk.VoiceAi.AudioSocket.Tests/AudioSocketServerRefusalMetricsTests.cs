using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.VoiceAi.AudioSocket.Diagnostics;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// The connection counters are process-wide and carry no tags, so their assertions run alone: this
/// collection is not parallelised with the rest of the assembly (the Ari precedent,
/// <c>AudioStreamMetricsGroup</c>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioSocketConnectionMetricsGroup
{
    public const string Name = "AudioSocketConnectionMetrics";
}

[Collection(AudioSocketConnectionMetricsGroup.Name)]
public sealed class AudioSocketServerRefusalMetricsTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HandleConnectionAsync_ShouldCountNoAcceptedConnection_WhenItRefusesOne()
    {
        // Arrange — one live session, then a connection over the limit
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30), MaxConcurrentSessions = 1 },
            NullLogger<AudioSocketServer>.Instance,
            new FakeTimeProvider());
        using var holderPeer = await ConnectAndIdentifyAsync(listener);
        using var holderEnd = await listener.AcceptTcpClientAsync();
        await server.HandleConnectionAsync(holderEnd, CancellationToken.None).WaitAsync(SignalTimeout);

        long accepted = 0, closed = 0;
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, AudioSocketMetrics.Meter)
                && instrument.Name is "audiosocket.connections.accepted" or "audiosocket.connections.closed")
                listener.EnableMeasurementEvents(instrument);
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "audiosocket.connections.accepted") Interlocked.Add(ref accepted, value);
            else Interlocked.Add(ref closed, value);
        });
        meters.Start();

        // Act
        using var refusedPeer = await ConnectAndIdentifyAsync(listener);
        using var refusedEnd = await listener.AcceptTcpClientAsync();
        await server.HandleConnectionAsync(refusedEnd, CancellationToken.None).WaitAsync(SignalTimeout);

        // Assert
        Interlocked.Read(ref accepted).Should().Be(
            0,
            "a refused connection was never a session: counting it accepted, with no close to balance it, makes accepted minus closed drift one up per refusal");
        Interlocked.Read(ref closed).Should().Be(0);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldCountNoAcceptedConnection_WhenItRefusesAnIdStillHeldAfterTheGrace()
    {
        // Arrange — one live session holding X, then a second connection presenting X that parks on it
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<AudioSocketServer>.Instance,
            time);
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        using var holderEnd = await listener.AcceptTcpClientAsync();
        await server.HandleConnectionAsync(holderEnd, CancellationToken.None).WaitAsync(SignalTimeout);
        await NextTimerAsync(time); // the holder's UUID deadline

        long accepted = 0, closed = 0;
        using var meters = ListenToConnectionCounters(
            onAccepted: value => Interlocked.Add(ref accepted, value),
            onClosed: value => Interlocked.Add(ref closed, value));

        using var refusedPeer = await ConnectAndIdentifyAsync(listener, x);
        using var refusedEnd = await listener.AcceptTcpClientAsync();
        var refusing = server.HandleConnectionAsync(refusedEnd, CancellationToken.None);
        await NextTimerAsync(time); // the second connection's UUID deadline
        var grace = await NextTimerAsync(time);

        // Act — the grace runs out with the holder still live
        time.Advance(grace.DueTime);
        await refusing.WaitAsync(SignalTimeout);

        // Assert
        server.ActiveSessionCount.Should().Be(1, "the holder keeps X and the refused connection never held it");
        Interlocked.Read(ref accepted).Should().Be(
            0,
            "a connection refused because its id stayed held was never a session, so it must not move accepted minus closed");
        Interlocked.Read(ref closed).Should().Be(0);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldStartNoSessionActivity_WhenItRefusesOne()
    {
        // Arrange — a listener that sees every session activity by the channel id it is tagged with. The
        // holder's own activity is the control that the listener sees a session at all.
        var holderId = Guid.NewGuid();
        var refusedId = Guid.NewGuid();
        var stopped = new ConcurrentQueue<string?>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, AudioSocketActivitySource.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Enqueue(activity.GetTagItem("asterisk.channel.id") as string),
        };
        ActivitySource.AddActivityListener(activities);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30), MaxConcurrentSessions = 1 },
            NullLogger<AudioSocketServer>.Instance,
            new FakeTimeProvider());
        using var holderPeer = await ConnectAndIdentifyAsync(listener, holderId);
        using var holderEnd = await listener.AcceptTcpClientAsync();
        await server.HandleConnectionAsync(holderEnd, CancellationToken.None).WaitAsync(SignalTimeout);

        // Act — a connection with another id, over the limit
        using var refusedPeer = await ConnectAndIdentifyAsync(listener, refusedId);
        using var refusedEnd = await listener.AcceptTcpClientAsync();
        await server.HandleConnectionAsync(refusedEnd, CancellationToken.None).WaitAsync(SignalTimeout);

        // Assert — each handler call has returned, so any activity it started has stopped
        stopped.Should().ContainSingle(
            id => id == holderId.ToString(),
            "the served connection opens its session activity, which shows the listener sees them");
        stopped.Should().NotContain(
            refusedId.ToString(),
            "a refused connection was never a session, so it opens no session span");
    }

    /// <summary>Listens to the two process-wide connection counters of this package's meter.</summary>
    private static MeterListener ListenToConnectionCounters(Action<long> onAccepted, Action<long> onClosed)
    {
        var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, AudioSocketMetrics.Meter)
                    && instrument.Name is "audiosocket.connections.accepted" or "audiosocket.connections.closed")
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "audiosocket.connections.accepted") onAccepted(value);
            else onClosed(value);
        });
        meters.Start();
        return meters;
    }

    /// <summary>The next timer created on <paramref name="time"/>, as soon as it exists.</summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    private static Task<TcpClient> ConnectAndIdentifyAsync(TcpListener listener) =>
        ConnectAndIdentifyAsync(listener, Guid.NewGuid());

    private static async Task<TcpClient> ConnectAndIdentifyAsync(TcpListener listener, Guid channelId)
    {
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var frame = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(frame, AudioSocketFrameType.Uuid, channelId.ToByteArray(bigEndian: true));
        await peer.GetStream().WriteAsync(frame.WrittenMemory);
        return peer;
    }
}
