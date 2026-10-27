using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// An event from the Realtime vendor that the bridge cannot read. The call it arrives on is still
/// being served: the transcript that follows is published, the audio that follows reaches the caller,
/// and the session is neither failed nor ended. An unreadable <c>error</c> event is reported exactly
/// as a readable one is, as one <see cref="RealtimeErrorEvent"/>, carrying the raw frame; anything
/// else is counted on <c>openai_realtime.frames.unparseable</c>, logged once at
/// <see cref="LogLevel.Warning"/> and skipped.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario sends its frames from the test after the client's <c>session.update</c>, in order:
/// the unreadable frame, then a final transcript, then one frame of assistant audio. The test waits for
/// the transcript and the audio to arrive, or for the session to end first, whichever comes sooner;
/// the bound on that wait is a failure, never a pace. The fake never answers a close, so the session is
/// ended by the test's own cancellation once the observation is taken.
/// </para>
/// <para>
/// The controls fence the fix: a readable event keeps its meaning, and an exception thrown by an event
/// subscriber still ends the session as it does today, uncounted, because only the decode is guarded.
/// </para>
/// </remarks>
public sealed class OpenAiRealtimeBridgeUnreadableEventTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";
    private const string Unparseable = "openai_realtime.frames.unparseable";
    private const string Failed = "openai_realtime.sessions.failed";

    private const string FinalTranscript = "the answer survives";

    private static readonly string FinalTranscriptEvent =
        $$"""{"type":"response.output_audio_transcript.done","transcript":"{{FinalTranscript}}"}""";

    /// <summary>One 20 ms frame of silent assistant audio at the Realtime API's 24 kHz.</summary>
    private static readonly string AssistantAudioDeltaEvent =
        $$"""{"type":"response.output_audio.delta","delta":"{{Convert.ToBase64String(new byte[960])}}"}""";

    // ── (a) the audio delta's member has the wrong type ────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldCountSkipAndKeepServing_WhenAnAudioDeltaCannotBeRead()
    {
        const string Unreadable = """{"type":"response.output_audio.delta","delta":42}""";

        var seen = await ServeAsync(Frames(Unreadable));

        using (new AssertionScope())
        {
            AssertServedPastOneCountedSkip(seen);
            seen.UnparseableTags.Should().HaveCount(1, "one unreadable frame is one measurement, whatever pass failed");
        }
    }

    // ── (b) an error event whose error cannot be read ──────────────────────────

    [Theory]
    [InlineData("""{"type":"error","error":{"message":{"code":"server_error","text":"boom"}}}""")]
    [InlineData("""{"type":"error","error":"boom"}""")]
    public async Task HandleSessionAsync_ShouldPublishOneErrorEventCarryingTheRawFrame_WhenAnErrorEventCannotBeRead(
        string unreadableError)
    {
        var seen = await ServeAsync(Frames(unreadableError));

        using (new AssertionScope())
        {
            AssertServedPastAnUnreadableError(seen, unreadableError);
            seen.Log.Count(e => e.Level == LogLevel.Warning).Should().Be(
                0, "a reported error is not also logged as a skipped frame");
        }
    }

    // ── (b′) the same unreadable error, fragmented on the wire ─────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldPublishOneErrorEventCarryingTheRawFrame_WhenAnUnreadableErrorEventArrivesInFragments()
    {
        const string Part1 = """{"type":"error","error":{"mess""";
        const string Part2 = """age":{"code":"server_error","te""";
        const string Part3 = """xt":"boom"}}}""";

        var seen = await ServeAsync(async fake =>
        {
            await fake.SendFragmentsAsync([Part1, Part2, Part3]);
            await fake.SendEventAsync(FinalTranscriptEvent);
            await fake.SendEventAsync(AssistantAudioDeltaEvent);
        });

        using (new AssertionScope())
        {
            AssertServedPastAnUnreadableError(seen, Part1 + Part2 + Part3);
            seen.Log.Count(e => e.Level == LogLevel.Warning).Should().Be(
                0, "a reported error is not also logged as a skipped frame");
        }
    }

    // ── (b″) control: a readable error keeps its message ───────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldPublishTheErrorMessage_WhenAnErrorEventIsReadable()
    {
        const string ReadableError = """{"type":"error","error":{"message":"rate limit exceeded"}}""";

        var seen = await ServeAsync(Frames(ReadableError));

        using (new AssertionScope())
        {
            seen.FaultText.Should().Be("none", "a readable error never failed the session");
            seen.EndedWhileServed.Should().BeFalse("a readable error leaves the call up");
            seen.Events.OfType<RealtimeErrorEvent>().Should().ContainSingle()
                .Which.Message.Should().Be("rate limit exceeded", "a readable error carries error.message");
            seen.Log.Count(e => e.EventId.Name == "OpenAiError").Should().Be(1);
            seen.FinalTranscripts.Should().Equal(FinalTranscript);
            seen.AudioReachedCaller.Should().BeTrue();
            seen.Failed.Should().Be(0);
            seen.Unparseable.Should().Be(0, "a readable error is not an unreadable frame");
        }
    }

    // ── (c) the first decode pass fails ────────────────────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldCountSkipAndKeepServing_WhenTheEventTypeCannotBeRead()
    {
        const string Unreadable = """{"type":42}""";

        var seen = await ServeAsync(Frames(Unreadable));

        using (new AssertionScope())
        {
            AssertServedPastOneCountedSkip(seen);
            seen.UnparseableTags.Should().HaveCount(1, "one unreadable frame is one measurement, whatever pass failed");
        }
    }

    // ── (d) control: a subscriber's exception is not a decode failure ──────────

    [Fact]
    public async Task HandleSessionAsync_ShouldFailUncounted_WhenAnEventSubscriberThrowsJsonException()
    {
        var thrown = new JsonException("thrown by a subscriber, not by the decode");

        var seen = await ServeAsync(
            Frames(),
            subscribe: events => events.Subscribe(e =>
            {
                if (e is RealtimeTranscriptEvent)
                    throw thrown;
            }));

        using (new AssertionScope())
        {
            seen.Fault.Should().BeSameAs(
                thrown, "an exception a subscriber throws propagates as it does today: only the decode is guarded");
            seen.Failed.Should().Be(1, "the subscriber's exception ends the session as a failure, as today");
            seen.Unparseable.Should().Be(0, "nothing the vendor sent was unreadable");
        }
    }

    // ── (e) control: readable events only ──────────────────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldServeWithNothingCounted_WhenEveryEventIsReadable()
    {
        var seen = await ServeAsync(Frames("""{"type":"response.output_audio_transcript.delta","delta":"the"}"""));

        using (new AssertionScope())
        {
            seen.FaultText.Should().Be("none", $"nothing failed the session (fault: {seen.FaultText})");
            seen.EndedWhileServed.Should().BeFalse();
            seen.FinalTranscripts.Should().Equal(FinalTranscript);
            seen.AudioReachedCaller.Should().BeTrue();
            seen.Failed.Should().Be(0);
            seen.Unparseable.Should().Be(0);
            seen.Log.Count(e => e.Level == LogLevel.Warning).Should().Be(0);
        }
    }

    // ── (g) the transcript delta's member has the wrong type ───────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldCountSkipAndKeepServing_WhenATranscriptDeltaCannotBeRead()
    {
        const string Unreadable = """{"type":"response.output_audio_transcript.delta","delta":42}""";

        var seen = await ServeAsync(Frames(Unreadable));

        using (new AssertionScope())
        {
            AssertServedPastOneCountedSkip(seen);
            seen.Events.OfType<RealtimeTranscriptEvent>().Should().OnlyContain(
                t => t.IsFinal, "the unreadable delta published nothing");
        }
    }

    // ── (h) the final transcript's member has the wrong type ───────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldCountSkipAndKeepServing_WhenAFinalTranscriptCannotBeRead()
    {
        const string Unreadable = """{"type":"response.output_audio_transcript.done","transcript":42}""";

        var seen = await ServeAsync(Frames(Unreadable));

        using (new AssertionScope())
        {
            AssertServedPastOneCountedSkip(seen);
            seen.UnparseableTags.Should().HaveCount(1, "one unreadable frame is one measurement, whatever pass failed");
        }
    }

    // ── (i) the function call's name has the wrong type ────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldCountSkipAndCallNothing_WhenAFunctionCallCannotBeRead()
    {
        const string Unreadable =
            """{"type":"response.function_call_arguments.done","call_id":"call_1","name":42,"arguments":"{}"}""";

        var seen = await ServeAsync(Frames(Unreadable));

        using (new AssertionScope())
        {
            AssertServedPastOneCountedSkip(seen);
            seen.Events.OfType<RealtimeFunctionCalledEvent>().Should().BeEmpty("no function was called");
            seen.VendorReceived.Should().NotContain(
                m => m.Contains("function_call_output", StringComparison.Ordinal),
                "nothing answers a call that could not be read");
        }
    }

    // ── The assembly pin ───────────────────────────────────────────────────────

    /// <summary>
    /// A fragmented final transcript is read as one message. A loop that parsed each read on its own would
    /// see the first fragment, which is not a whole JSON document, and the whole transcript never.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldPublishTheWholeTranscript_WhenTheFinalTranscriptArrivesInFragments()
    {
        const string Part1 = """{"type":"response.output_audio_transcript.done","transcript":"the ans""";
        const string Part2 = """wer surv""";
        const string Part3 = """ives"}""";

        var seen = await ServeAsync(async fake =>
        {
            await fake.SendFragmentsAsync([Part1, Part2, Part3]);
            await fake.SendEventAsync(AssistantAudioDeltaEvent);
        });

        using (new AssertionScope())
        {
            seen.FaultText.Should().Be("none", $"nothing failed the session (fault: {seen.FaultText})");
            seen.FinalTranscripts.Should().Equal(
                [FinalTranscript], "the three fragments are one message, published once and whole");
            seen.AudioReachedCaller.Should().BeTrue();
        }
    }

    // ── (f) the same unreadable delta behind the session broker ────────────────

    /// <summary>
    /// The unreadable audio delta with the bridge running under <see cref="VoiceAiSessionBroker"/> and a
    /// caller on the line. The caller reads until the assistant's next audio frame or the end of the
    /// connection. A served call delivers the audio and no hangup frame.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldSendNoHangupFrame_WhenAnUnreadableEventArrivesBehindTheBroker()
    {
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilClientCloses = true };
        fakeOpenAi.EventsToSend.Add("""{"type":"response.output_audio.delta","delta":42}""");
        fakeOpenAi.EventsToSend.Add(AssistantAudioDeltaEvent);
        fakeOpenAi.Start();

        using var metrics = new MeterCapture(MeterName);
        await using var bridge = CreateBridge(fakeOpenAi, NullLogger<OpenAiRealtimeBridge>.Instance);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        using var broker = new VoiceAiSessionBroker(server, bridge, NullLogger<VoiceAiSessionBroker>.Instance);
        await server.StartAsync(CancellationToken.None);
        await broker.StartAsync(CancellationToken.None);

        using var caller = await RawCaller.ConnectAsync(server.BoundPort);
        var read = await caller.ReadUntilAudioOrEndAsync(SignalTimeout);
        var failedWhileServed = metrics.Get(Failed);

        // The caller leaves, which ends the served session; then the broker stops.
        await caller.SendHangupAsync();
        await broker.StopAsync(CancellationToken.None);

        using (new AssertionScope())
        {
            read.Frames.Should().NotContain(
                f => f.Type == AudioSocketFrameType.Hangup,
                $"no hangup frame is sent while the call is served (read: {read.Describe()})");
            read.ReachedEnd.Should().BeFalse($"the line stays up (read: {read.Describe()})");
            read.Frames.Should().Contain(
                f => f.Type == AudioSocketFrameType.Audio,
                $"the audio after the unreadable event reaches the caller (read: {read.Describe()})");
            failedWhileServed.Should().Be(0, "the session did not fail");
        }
    }

    // ── Shared assertions ──────────────────────────────────────────────────────

    private static void AssertServedPastOneCountedSkip(Observation seen)
    {
        seen.FaultText.Should().Be("none", $"the unreadable event is skipped, not thrown (fault: {seen.FaultText})");
        seen.EndedWhileServed.Should().BeFalse("the session goes on while the caller is on the line");
        seen.FinalTranscripts.Should().Equal(
            [FinalTranscript], "the transcript after the unreadable event is published");
        seen.AudioReachedCaller.Should().BeTrue("the audio after the unreadable event reaches the caller");
        seen.Failed.Should().Be(0, "an unreadable event does not fail the session");
        seen.Unparseable.Should().Be(1, "the skipped event is counted once");
        seen.UnparseableTags.Should().OnlyContain(
            t => t.Contains("voiceai.provider=", StringComparison.Ordinal), "the counter names its provider");
        seen.Log.Count(e => e.Level == LogLevel.Warning).Should().Be(1, "the skipped event is logged once at Warning");
        seen.Events.OfType<RealtimeErrorEvent>().Should().BeEmpty("an unreadable event that names no failure is no error");
    }

    private static void AssertServedPastAnUnreadableError(Observation seen, string rawFrame)
    {
        seen.FaultText.Should().Be("none", $"an unreadable error is reported, not thrown (fault: {seen.FaultText})");
        seen.EndedWhileServed.Should().BeFalse("a readable error leaves the call up, so an unreadable one does too");
        seen.Events.OfType<RealtimeErrorEvent>().Should().ContainSingle(
            "the unreadable error is reported once, as a readable one is")
            .Which.Message.Should().Be(rawFrame, "the event carries the raw frame");
        seen.Log.Count(e => e.EventId.Name == "OpenAiError").Should().Be(1, "the error is logged as a readable one is");
        seen.FinalTranscripts.Should().Equal([FinalTranscript]);
        seen.AudioReachedCaller.Should().BeTrue();
        seen.Failed.Should().Be(0, "an unreadable error is no more fatal than a readable one");
        seen.Unparseable.Should().Be(0, "an error the vendor sent is reported, not counted as unreadable");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    /// <summary>The given frames, then the final transcript, then one frame of assistant audio.</summary>
    private static Func<RealtimeFakeServer, Task> Frames(params string[] first) => async fake =>
    {
        foreach (var frame in first)
            await fake.SendEventAsync(frame);
        await fake.SendEventAsync(FinalTranscriptEvent);
        await fake.SendEventAsync(AssistantAudioDeltaEvent);
    };

    private sealed record Observation(
        Exception? Fault,
        bool EndedWhileServed,
        IReadOnlyList<RealtimeEvent> Events,
        IReadOnlyList<string> FinalTranscripts,
        bool AudioReachedCaller,
        long Failed,
        long Unparseable,
        IReadOnlyList<string> UnparseableTags,
        IReadOnlyList<RecordingLogger<OpenAiRealtimeBridge>.LogEntry> Log,
        IReadOnlyList<string> VendorReceived)
    {
        /// <summary>The fault's type and message, without its stack, for a failure message.</summary>
        public string FaultText => Fault is null ? "none" : $"{Fault.GetType().FullName}: {Fault.Message}";
    }

    /// <summary>
    /// Runs one session against the fake, has <paramref name="send"/> speak for the vendor, and returns what
    /// was seen once the final transcript and the caller's audio have both arrived, or the session ended
    /// first. The session is then cancelled and the caller's side released.
    /// </summary>
    private static async Task<Observation> ServeAsync(
        Func<RealtimeFakeServer, Task> send,
        Func<IObservable<RealtimeEvent>, IDisposable>? subscribe = null)
    {
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        var log = new RecordingLogger<OpenAiRealtimeBridge>();
        await using var bridge = CreateBridge(fakeOpenAi, log);
        using var metrics = new MeterCapture(MeterName);
        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeTranscriptEvent>().Any(t => t.IsFinal));
        using var extra = subscribe?.Invoke(bridge.Events);
        using var cts = new CancellationTokenSource();
        using var readCts = new CancellationTokenSource();

        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        var audioHeard = FirstAudioAsync(client, readCts.Token);
        await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);

        try
        {
            await send(fakeOpenAi);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The session ended under the vendor's sends: a frame after the bridge's close has no reader.
            // The ending is what the assertions report.
        }

        var served = Task.WhenAll(collector.Satisfied, audioHeard);
        try
        {
            await Task.WhenAny(served, sessionTask).WaitAsync(SignalTimeout);
        }
        catch (TimeoutException)
        {
            // Neither arrived: reported by the assertions on what was seen.
        }

        var endedWhileServed = sessionTask.IsCompleted;
        var observation = new Observation(
            Fault: sessionTask.IsFaulted ? sessionTask.Exception!.InnerException : null,
            EndedWhileServed: endedWhileServed,
            Events: collector.Events,
            FinalTranscripts: [.. collector.Events.OfType<RealtimeTranscriptEvent>().Where(t => t.IsFinal).Select(t => t.Transcript)],
            AudioReachedCaller: audioHeard.IsCompletedSuccessfully && audioHeard.Result,
            Failed: metrics.Get(Failed),
            Unparseable: metrics.Get(Unparseable),
            UnparseableTags: metrics.Tags(Unparseable),
            Log: log.Entries,
            VendorReceived: fakeOpenAi.ReceivedMessages);

        // End the session and release the caller's side.
        await cts.CancelAsync();
        await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));
        await readCts.CancelAsync();
        await Record.ExceptionAsync(() => audioHeard.WaitAsync(SignalTimeout));
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
        return observation;
    }

    /// <summary>True once the caller has read one audio frame; false if its stream ended or was cancelled first.</summary>
    private static async Task<bool> FirstAudioAsync(AudioSocketClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in client.ReadAudioAsync(ct))
                return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The observation was taken without audio.
        }

        return false;
    }

    private static async Task<(AudioSocketSession session, AudioSocketServer audioServer, AudioSocketClient client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var tcs = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        audioServer.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(SignalTimeout);
        return (session, audioServer, client);
    }

    private static OpenAiRealtimeBridge CreateBridge(RealtimeFakeServer fakeOpenAi, ILogger<OpenAiRealtimeBridge> logger)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-realtime",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        return new OpenAiRealtimeBridge(options, new RealtimeFunctionRegistry([]), logger)
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

        public async Task SendHangupAsync()
        {
            try
            {
                await _stream.WriteAsync(new byte[] { (byte)AudioSocketFrameType.Hangup, 0, 0 });
            }
            catch (IOException)
            {
                // The line is already gone; there is nobody to hang up on.
            }
        }

        /// <summary>
        /// Reads frames until the first audio frame or the end of the connection (a hangup frame is read past,
        /// so that what follows it is seen), or until <paramref name="bound"/> runs out with the line silent;
        /// what was read is returned either way.
        /// </summary>
        public async Task<CallerRead> ReadUntilAudioOrEndAsync(TimeSpan bound)
        {
            var received = new List<byte>();
            using var cts = new CancellationTokenSource();
            var reachedEnd = false;
            var reading = ReadAsync(received, cts.Token);
            try
            {
                reachedEnd = await reading.WaitAsync(bound);
            }
            catch (TimeoutException)
            {
                // A silent line: reported by the assertions, with what was read.
                await cts.CancelAsync();
                await Record.ExceptionAsync(() => reading);
            }

            byte[] bytes;
            lock (received)
                bytes = [.. received];
            return new CallerRead(reachedEnd, CallerFrame.Parse(bytes), bytes.Length);
        }

        // True when the connection ended; false when an audio frame stopped the read.
        private async Task<bool> ReadAsync(List<byte> received, CancellationToken ct)
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = await _stream.ReadAsync(buffer, ct)) > 0)
                {
                    lock (received)
                    {
                        received.AddRange(buffer.AsSpan(0, n).ToArray());
                        if (CallerFrame.Parse([.. received]).Any(f => f.Type == AudioSocketFrameType.Audio))
                            return false;
                    }
                }
            }
            catch (IOException)
            {
                // A reset is also an end of the connection; the bytes before it are kept.
            }

            return true;
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
            (ReachedEnd ? "connection ended; " : "connection open; ")
            + (ByteCount == 0
                ? "no bytes"
                : $"{ByteCount} bytes: " + string.Join(", ", Frames.Select(f => $"{f.Type}({f.Length})")));
    }
}
