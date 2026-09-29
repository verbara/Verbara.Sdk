using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
            if (ReferenceEquals(instrument.Meter, Diagnostics.AudioSocketMetrics.Meter)
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

    private static async Task<TcpClient> ConnectAndIdentifyAsync(TcpListener listener)
    {
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var frame = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(frame, AudioSocketFrameType.Uuid, Guid.NewGuid().ToByteArray(bigEndian: true));
        await peer.GetStream().WriteAsync(frame.WrittenMemory);
        return peer;
    }
}
