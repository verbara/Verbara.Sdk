using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// What the caller's line does when the Realtime vendor ends a session behind the session broker: the
/// vendor sends its answer and closes its WebSocket with <c>1000</c> while the caller is still on the
/// line, the bridge returns from <see cref="OpenAiRealtimeBridge.HandleSessionAsync"/>, and the broker
/// ends the line — the caller reads the assistant's audio, then one hangup frame, then the end of the
/// connection, instead of a line left open in silence.
/// </summary>
/// <remarks>
/// Real AudioSocket server, real broker, real bridge, with the bridge's vendor endpoint pointed at the
/// loopback fake (<see cref="RealtimeFakeServer"/>, which closes with <c>1000 "done"</c> as soon as its
/// burst is out). The caller is a raw socket that sends the identifying frame and then only reads. The
/// read to the end of the stream is bounded by <see cref="SignalTimeout"/>, whose expiry is a failure
/// (a line left open), never a pace.
/// </remarks>
public sealed class OpenAiRealtimeBridgeBrokerEndingTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One 20 ms frame of assistant audio: 480 silent samples at the Realtime API's 24 kHz, which the
    /// bridge turns into one frame at the session's 8 kHz.
    /// </summary>
    private static readonly string AssistantAudioDeltaEvent =
        $$"""{"type":"response.output_audio.delta","delta":"{{Convert.ToBase64String(new byte[960])}}"}""";

    [Fact]
    public async Task HandleSessionAsync_ShouldBeFollowedByOneHangupFrameAndTheClose_WhenTheVendorClosesWhileTheCallerIsOnTheLine()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(AssistantAudioDeltaEvent);
        fakeOpenAi.EventsToSend.Add(AssistantAudioDeltaEvent);
        fakeOpenAi.EventsToSend.Add(AssistantAudioDeltaEvent);
        fakeOpenAi.Start();

        await using var bridge = CreateBridge(fakeOpenAi);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        using var broker = new VoiceAiSessionBroker(server, bridge, NullLogger<VoiceAiSessionBroker>.Instance);
        await server.StartAsync(CancellationToken.None);
        await broker.StartAsync(CancellationToken.None);

        using var caller = await RawCaller.ConnectAsync(server.BoundPort);
        var read = await caller.ReadToEndAsync(SignalTimeout);

        using (new AssertionScope())
        {
            read.ReachedEnd.Should().BeTrue(
                "the vendor ended the session and the bridge returned, so the broker ends the line: the caller " +
                $"reads the end of the connection within {SignalTimeout.TotalSeconds:0} s instead of a line left " +
                $"open in silence (read so far: {read.Describe()})");
            read.Frames.Count(f => f.Type == AudioSocketFrameType.Hangup).Should().Be(
                1, $"the broker's ending writes exactly one hangup frame (read: {read.Describe()})");
            read.Frames.Should().Contain(
                f => f.Type != AudioSocketFrameType.Hangup,
                $"the assistant's audio reached the caller before the ending (read: {read.Describe()})");
            if (read.Frames.Count > 0)
                read.Frames[^1].Type.Should().Be(
                    AudioSocketFrameType.Hangup, "the hangup frame is the last thing the caller reads");
        }
    }

    private static OpenAiRealtimeBridge CreateBridge(RealtimeFakeServer fakeOpenAi)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-realtime",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        return new OpenAiRealtimeBridge(
            options,
            new RealtimeFunctionRegistry([]),
            NullLogger<OpenAiRealtimeBridge>.Instance)
        {
            BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/"),
        };
    }

    /// <summary>A caller that speaks the AudioSocket wire directly, as Asterisk does.</summary>
    private sealed class RawCaller : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private RawCaller(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawCaller> ConnectAsync(int port)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var caller = new RawCaller(client);
                var uuid = new byte[19];
                uuid[0] = (byte)AudioSocketFrameType.Uuid;
                uuid[2] = 16;
                Guid.NewGuid().TryWriteBytes(uuid.AsSpan(3), bigEndian: true, out _);
                await caller._stream.WriteAsync(uuid);
                return caller;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The caller never reached the server: release its socket before reporting the failure.
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Reads until the server ends the stream or <paramref name="bound"/> runs out; what was read is
        /// returned either way, so a line left open fails on its assertion rather than on a timeout.
        /// </summary>
        public async Task<CallerRead> ReadToEndAsync(TimeSpan bound)
        {
            var received = new List<byte>();
            var reachedEnd = false;
            var reading = ReadAllAsync(_stream, received);
            try
            {
                await reading.WaitAsync(bound);
                reachedEnd = true;
            }
            catch (TimeoutException)
            {
                // The line is still open: reported by the assertion, with what was read.
            }

            byte[] bytes;
            lock (received)
                bytes = [.. received];
            return new CallerRead(reachedEnd, CallerFrame.Parse(bytes), bytes.Length);
        }

        private static async Task ReadAllAsync(NetworkStream stream, List<byte> received)
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    lock (received)
                        received.AddRange(buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (IOException)
            {
                // A reset is also an end of the connection; the bytes before it are kept.
            }
            catch (ObjectDisposedException)
            {
                // The caller was disposed while a read was still pending on a line left open.
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }

    private sealed record CallerFrame(AudioSocketFrameType Type, int Length)
    {
        public static List<CallerFrame> Parse(byte[] bytes)
        {
            var frames = new List<CallerFrame>();
            var offset = 0;
            while (bytes.Length - offset >= 3)
            {
                var length = (bytes[offset + 1] << 8) | bytes[offset + 2];
                if (bytes.Length - offset - 3 < length)
                    break;
                frames.Add(new CallerFrame((AudioSocketFrameType)bytes[offset], length));
                offset += 3 + length;
            }

            return frames;
        }
    }

    private sealed record CallerRead(bool ReachedEnd, IReadOnlyList<CallerFrame> Frames, int ByteCount)
    {
        public string Describe() =>
            ByteCount == 0
                ? "no bytes"
                : $"{ByteCount} bytes: " + string.Join(", ", Frames.Select(f => $"{f.Type}({f.Length})"));
    }
}
