using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Verbara.Sdk.VoiceAi.DependencyInjection;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// A host's stop and the session handlers its broker started: while the shutdown is graceful, the stop
/// does not complete before those handlers have returned, whichever order the host stops the broker and
/// the AudioSocket server in, and also when it stops them concurrently. Once the shutdown is no longer
/// graceful, the stop does not wait for a handler that ignores its token.
/// </summary>
/// <remarks>
/// <para>
/// A real host (<see cref="Host.CreateApplicationBuilder()"/>) with <c>AddAudioSocketServer</c> and
/// <c>AddVoiceAiPipeline</c> on <c>127.0.0.1</c> port 0, and a raw socket as the far end. The handler
/// parks on a gate the test holds and ignores both its token and its session ending, so the only thing
/// that can end it is the test opening the gate.
/// </para>
/// <para>
/// No assertion here reads "the stop has not completed" at an instant the test picked. The broker
/// raises an edge when its stop starts waiting for its handlers: an internal
/// <see cref="Task"/>-typed member named <see cref="WaitingForHandlersMember"/>, read by reflection
/// before the stop is called, and a task that never completes when the broker has no such member. The
/// test awaits whichever comes first, the host's stop or that edge. A stop that does not wait completes
/// first, with the gate still closed. A stop that waits cannot complete while the gate is closed, so
/// once the edge has completed, the stop is still running by construction. Every bound below is a
/// failure bound, never a pace.
/// </para>
/// </remarks>
public sealed class VoiceAiSessionBrokerShutdownTests : IAsyncLifetime
{
    /// <summary>
    /// The broker's internal task that completes when its stop starts waiting for the handlers it started
    /// (after it has ended their live sessions). It exists from the broker's construction, so it can be
    /// read before the stop is called.
    /// </summary>
    internal const string WaitingForHandlersMember = "WaitingForHandlers";

    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly List<IHost> _hosts = [];

    public enum StopOrder
    {
        /// <summary>The server is registered first, so the host stops the broker first, then the server.</summary>
        BrokerStoppedFirst,

        /// <summary>The broker is registered first, so the host stops the server first, then the broker.</summary>
        ServerStoppedFirst,

        /// <summary>The server is registered first and the host stops its services concurrently.</summary>
        StoppedConcurrently,
    }

    [Theory]
    [InlineData(StopOrder.BrokerStoppedFirst)]
    [InlineData(StopOrder.ServerStoppedFirst)]
    [InlineData(StopOrder.StoppedConcurrently)]
    public async Task StopAsync_ShouldCompleteOnlyAfterTheHandlerHasReturned_WhenTheShutdownIsGraceful(StopOrder order)
    {
        var handler = new GatedSessionHandler();
        var host = await StartHostAsync(handler, order);
        var broker = host.Services.GetRequiredService<VoiceAiSessionBroker>();
        var server = host.Services.GetRequiredService<AudioSocketServer>();
        using var peer = await RawPeer.ConnectAsync(server.BoundPort);
        var call = await handler.Entered.WaitAsync(SignalTimeout);
        var waiting = WaitingForHandlers(broker);
        using var grace = new CancellationTokenSource();

        var stop = host.StopAsync(grace.Token);
        var handlerReturnedBeforeTheStop = stop.ContinueWith(
            _ => handler.Returned.IsCompleted,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            await Task.WhenAny(stop, waiting).WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                handler.Returned.IsCompleted.Should().BeFalse("the gate is still closed, so the handler is still running");
                waiting.IsCompleted.Should().BeTrue(
                    "a graceful stop waits for the handlers its broker started, and the broker says so when it starts waiting");
                stop.IsCompleted.Should().BeFalse(
                    $"the host's stop ({order}) does not complete while a handler the broker started is still running");
                call.Token.IsCancellationRequested.Should().BeFalse(
                    "the shutdown is still graceful, so the handler's token is not cancelled");
            }

            if (order == StopOrder.BrokerStoppedFirst)
            {
                // The server's stop comes after the broker's, which cannot complete while the gate is
                // closed: whatever the far end reads now, the broker's stop wrote.
                var read = await peer.ReadToEndAsync(SignalTimeout);
                AssertOneHangupThenClose(read, "the broker's graceful stop ends the live session before the server's stop runs");
            }
        }
        finally
        {
            handler.Release();
        }

        await stop.WaitAsync(SignalTimeout);

        (await handlerReturnedBeforeTheStop).Should().BeTrue(
            "the host's stop completed only after the handler had returned");
        if (order != StopOrder.BrokerStoppedFirst)
        {
            var read = await peer.ReadToEndAsync(SignalTimeout);
            read.Frames.Count(f => f.Type == AudioSocketFrameType.Hangup).Should().BeLessThanOrEqualTo(
                1, $"some ending wrote at most one hangup frame in all (read: {read.Describe()})");
        }
    }

    [Fact]
    public async Task StopAsync_ShouldNotWaitForTheHandler_WhenTheShutdownBudgetHasRunOut()
    {
        var handler = new GatedSessionHandler();
        var host = await StartHostAsync(handler, StopOrder.BrokerStoppedFirst);
        var server = host.Services.GetRequiredService<AudioSocketServer>();
        using var peer = await RawPeer.ConnectAsync(server.BoundPort);
        var call = await handler.Entered.WaitAsync(SignalTimeout);

        try
        {
            // A host whose shutdown budget is gone hands its stop a token that is already cancelled.
            var stop = () => host.StopAsync(new CancellationToken(canceled: true)).WaitAsync(SignalTimeout);

            await stop.Should().NotThrowAsync(
                "a stop that is no longer graceful does not wait for a handler that ignores its token");
            using (new AssertionScope())
            {
                handler.Returned.IsCompleted.Should().BeFalse(
                    "the handler ignores its token and its gate is still closed, so it has not returned");
                call.Token.IsCancellationRequested.Should().BeTrue(
                    "the host withdrew its grace, so the broker cancelled the token its handlers run under");
            }
        }
        finally
        {
            handler.Release();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in Enumerable.Reverse(_hosts))
        {
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                host.Dispose();
        }
    }

    /// <summary>Builds and starts a host with the server and the broker over <paramref name="handler"/>.</summary>
    private async Task<IHost> StartHostAsync(GatedSessionHandler handler, StopOrder order)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ServicesStopConcurrently = order == StopOrder.StoppedConcurrently);

        // Registered before AddVoiceAiPipeline, whose TryAdd then keeps it: this is the handler the
        // broker runs. A host stops its hosted services in the reverse of their registration order.
        builder.Services.AddSingleton<ISessionHandler>(handler);
        if (order == StopOrder.ServerStoppedFirst)
        {
            builder.Services.AddVoiceAiPipeline<NoConversation>();
            builder.Services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
        }
        else
        {
            builder.Services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
            builder.Services.AddVoiceAiPipeline<NoConversation>();
        }

        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(CancellationToken.None).WaitAsync(SignalTimeout);
        return host;
    }

    /// <summary>
    /// The broker's "now waiting for handlers" task, read by reflection; a task that never completes when
    /// the broker has no such member.
    /// </summary>
    private static Task WaitingForHandlers(VoiceAiSessionBroker broker)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(VoiceAiSessionBroker);
        var value = type.GetProperty(WaitingForHandlersMember, flags)?.GetValue(broker)
            ?? type.GetField(WaitingForHandlersMember, flags)?.GetValue(broker);
        return value as Task ?? new TaskCompletionSource().Task;
    }

    private static void AssertOneHangupThenClose(PeerRead read, string because)
    {
        using var scope = new AssertionScope();
        read.ReachedEnd.Should().BeTrue(
            $"{because}: the far end reads the end of the connection (read so far: {read.Describe()})");
        read.Frames.Count(f => f.Type == AudioSocketFrameType.Hangup).Should().Be(
            1, $"{because}: exactly one hangup frame (read: {read.Describe()})");
        if (read.Frames.Count > 0)
            read.Frames[^1].Type.Should().Be(
                AudioSocketFrameType.Hangup, "the hangup frame is the last thing the far end reads");
    }

    /// <summary>
    /// Records its call on entry, then waits for the test's gate and nothing else: it ignores its token
    /// and its session ending, like a handler stuck in a provider call that does not honour cancellation.
    /// </summary>
    private sealed class GatedSessionHandler : ISessionHandler
    {
        private readonly TaskCompletionSource<ParkedCall> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes with the first call, as soon as it is recorded.</summary>
        public Task<ParkedCall> Entered => _entered.Task;

        /// <summary>Completes when the handler has returned.</summary>
        public Task Returned => _returned.Task;

        /// <summary>Opens the gate. Safe to call more than once.</summary>
        public void Release() => _gate.TrySetResult();

        public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
        {
            _entered.TrySetResult(new ParkedCall(session.ChannelId, ct));
            try
            {
                await _gate.Task.ConfigureAwait(false);
            }
            finally
            {
                _returned.TrySetResult();
            }
        }
    }

    /// <summary>The conversation handler <c>AddVoiceAiPipeline</c> needs a type for. No session reaches it.</summary>
    private sealed class NoConversation : IConversationHandler
    {
        public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(string.Empty);
    }

    /// <summary>A far end that speaks the AudioSocket wire directly, as Asterisk does.</summary>
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly List<byte> _received = [];
        private readonly Task _reading;

        private RawPeer(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            _reading = ReadAllAsync();
        }

        public static async Task<RawPeer> ConnectAsync(int port)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var uuid = new byte[19];
                uuid[0] = (byte)AudioSocketFrameType.Uuid;
                uuid[2] = 16;
                Guid.NewGuid().TryWriteBytes(uuid.AsSpan(3), bigEndian: true, out _);
                await client.GetStream().WriteAsync(uuid);
                return new RawPeer(client);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The peer never reached the server: release its socket before reporting the failure.
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Waits until the server ends the stream or <paramref name="bound"/> runs out; what was read is
        /// returned either way, so a line left open fails on its assertion rather than on a timeout.
        /// </summary>
        public async Task<PeerRead> ReadToEndAsync(TimeSpan bound)
        {
            var reachedEnd = false;
            try
            {
                await _reading.WaitAsync(bound);
                reachedEnd = true;
            }
            catch (TimeoutException)
            {
                // The line is still open: reported by the caller's assertion, with what was read.
            }

            byte[] bytes;
            lock (_received)
                bytes = [.. _received];
            return new PeerRead(reachedEnd, PeerFrame.Parse(bytes), bytes.Length);
        }

        private async Task ReadAllAsync()
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = await _stream.ReadAsync(buffer)) > 0)
                {
                    lock (_received)
                        _received.AddRange(buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (IOException)
            {
                // A reset is also an end of the connection; the bytes before it are kept.
            }
            catch (ObjectDisposedException)
            {
                // The peer was disposed while a read was still pending on a line left open.
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }

    private sealed record PeerFrame(AudioSocketFrameType Type, int Length)
    {
        public static List<PeerFrame> Parse(byte[] bytes)
        {
            var frames = new List<PeerFrame>();
            var offset = 0;
            while (bytes.Length - offset >= 3)
            {
                var length = (bytes[offset + 1] << 8) | bytes[offset + 2];
                if (bytes.Length - offset - 3 < length)
                    break;
                frames.Add(new PeerFrame((AudioSocketFrameType)bytes[offset], length));
                offset += 3 + length;
            }

            return frames;
        }
    }

    private sealed record PeerRead(bool ReachedEnd, IReadOnlyList<PeerFrame> Frames, int ByteCount)
    {
        public string Describe() =>
            ByteCount == 0
                ? "no bytes"
                : $"{ByteCount} bytes: " + string.Join(", ", Frames.Select(f => $"{f.Type}({f.Length})"));
    }
}
