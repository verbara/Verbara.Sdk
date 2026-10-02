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
/// A Realtime session that a host's shutdown cancels while it is still unwinding, and whose bridge the
/// host's container then releases. Whatever the session still raises after that release is dropped and
/// logged at Debug: it raises no <see cref="ObjectDisposedException"/> inside the session, logs nothing at
/// Error, and is not counted as a failed session. A session the host cancelled stays completed.
/// </summary>
/// <remarks>
/// <para>
/// Real AudioSocket server, real broker, real bridge pointed at the loopback fake vendor, and the host's
/// shutdown played as the host plays it: the broker's stop with a token that is already cancelled (the
/// budget is gone), the broker's disposal, then the bridge's disposal — the container releases the
/// broker before the bridge, because the broker was built from it.
/// </para>
/// <para>
/// A session the host cancelled checks its token before it raises a function call's event, so a
/// function that returns after the cancellation never reaches the raise (the second row pins that). The
/// window is the one after that check: the function returned while the shutdown was still graceful, and
/// the session is past its token check, under its write lock, when the host's shutdown lands. The
/// session reaches that point after a caller who hung up while the function ran, where it logs that the
/// function's result was not sent; the test's logger holds the session there, on that entry, while the
/// host cancels and releases the bridge, then lets it go on to raise the call's event. Every wait is
/// bounded by <see cref="SignalTimeout"/> as a failure bound, never as a pace.
/// </para>
/// </remarks>
public sealed class OpenAiRealtimeBridgeHostShutdownTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    [Fact]
    public async Task HandleSessionAsync_ShouldDropTheEventAndCountNoFailure_WhenTheSessionRaisesItAfterTheHostReleasedTheBridge()
    {
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.EventsToSend.Add(ParkedFunction.CallEvent);
        fakeOpenAi.Start();
        var function = new ParkedFunction();
        var log = new HoldingLogger(holdOn: "FunctionResultNotSent");
        using var metrics = new MeterCapture(MeterName);
        var bridge = CreateBridge(fakeOpenAi, log, function);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        var broker = new VoiceAiSessionBroker(server, bridge, NullLogger<VoiceAiSessionBroker>.Instance);
        try
        {
            await server.StartAsync(CancellationToken.None);
            await broker.StartAsync(CancellationToken.None);
            await using var caller = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
            await caller.ConnectAsync(CancellationToken.None);
            await function.Started.WaitAsync(SignalTimeout);

            // The caller hangs up while the function runs: the bridge sends its close to the vendor.
            await caller.SendHangupAsync();
            await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);

            // The function returns while the shutdown is still graceful; the session takes its write lock,
            // sees its close already out, and logs that the result was not sent, where the logger holds it.
            function.Release();
            await log.Held.WaitAsync(SignalTimeout);

            // The host's shutdown, as the host runs it: the broker's stop with its budget gone, then the
            // container's disposal, broker first, then the bridge.
            await broker.StopAsync(new CancellationToken(canceled: true));
            broker.Dispose();
            await bridge.DisposeAsync();
            log.Proceed();
            await log.SessionEnded.WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    0, "an event raised after the host released the bridge is not the session failing");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "a session the host cancelled is counted completed");
                log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).Should().BeEmpty(
                    "nothing is logged at Error for a session the host cancelled");
                log.Entries.Count(e => e.Exception is ObjectDisposedException).Should().Be(
                    0, "no ObjectDisposedException is raised inside the session");
                log.Entries.Count(e => e.Level == LogLevel.Debug).Should().BePositive(
                    "the event raised after the bridge's disposal is dropped and the drop is logged at Debug");
            }
        }
        finally
        {
            function.Release();
            log.Proceed();
            broker.Dispose();
            await bridge.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCountNoFailure_WhenAFunctionReturnsAfterTheHostCancelledAndReleasedTheBridge()
    {
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(ParkedFunction.CallEvent);
        fakeOpenAi.Start();
        var function = new ParkedFunction();
        var log = new HoldingLogger(holdOn: null);
        using var metrics = new MeterCapture(MeterName);
        var bridge = CreateBridge(fakeOpenAi, log, function);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        var broker = new VoiceAiSessionBroker(server, bridge, NullLogger<VoiceAiSessionBroker>.Instance);
        try
        {
            await server.StartAsync(CancellationToken.None);
            await broker.StartAsync(CancellationToken.None);
            await using var caller = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
            await caller.ConnectAsync(CancellationToken.None);
            await function.Started.WaitAsync(SignalTimeout);

            // The host's shutdown lands while the function, which ignores its token, still runs.
            await broker.StopAsync(new CancellationToken(canceled: true));
            broker.Dispose();
            await bridge.DisposeAsync();
            function.Release();
            await log.SessionEnded.WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    0, "a function that returns after the host cancelled the session does not fail it");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "a session the host cancelled is counted completed");
                log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).Should().BeEmpty(
                    "nothing is logged at Error for a session the host cancelled");
            }
        }
        finally
        {
            function.Release();
            broker.Dispose();
            await bridge.DisposeAsync();
        }
    }

    private static OpenAiRealtimeBridge CreateBridge(
        RealtimeFakeServer fakeOpenAi, ILogger<OpenAiRealtimeBridge> logger, IRealtimeFunctionHandler function)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-realtime",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        return new OpenAiRealtimeBridge(options, new RealtimeFunctionRegistry([function]), logger)
        {
            BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/"),
        };
    }

    /// <summary>
    /// A function the vendor calls once per session. It reports when it starts and then waits for the
    /// test's gate and nothing else: it ignores its token, like a tool call stuck in a backend that does not
    /// honour cancellation.
    /// </summary>
    private sealed class ParkedFunction : IRealtimeFunctionHandler
    {
        public const string FunctionName = "lookup_order";

        /// <summary>The vendor's request for this function, as it arrives on the wire.</summary>
        public const string CallEvent =
            """{"type":"response.function_call_arguments.done","call_id":"call-parked","name":"lookup_order","arguments":"{}"}""";

        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => FunctionName;

        public string Description => "Looks up an order, for as long as the test says";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        /// <summary>Completes when the bridge has called the function.</summary>
        public Task Started => _started.Task;

        /// <summary>Opens the gate. Safe to call more than once.</summary>
        public void Release() => _gate.TrySetResult();

        public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _started.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            return """{"status":"shipped"}""";
        }
    }

    /// <summary>
    /// The bridge's logger. It keeps every entry, signals when the session's end is logged, and, when
    /// told which entry to hold on, holds the logging thread inside that entry until the test lets it go:
    /// the session is then at a known point, under its write lock, for as long as the test needs.
    /// </summary>
    private sealed class HoldingLogger(string? holdOn) : ILogger<OpenAiRealtimeBridge>
    {
        private readonly List<Entry> _entries = [];
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the logging thread is held inside the entry named at construction.</summary>
        public Task Held => _held.Task;

        /// <summary>Completes once the bridge has logged the end of a session.</summary>
        public Task SessionEnded => _sessionEnded.Task;

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        /// <summary>Lets a held logging thread go on. Safe to call more than once.</summary>
        public void Proceed() => _proceed.TrySetResult();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add(new Entry(logLevel, eventId, formatter(state, exception), exception));

            if (eventId.Name == "SessionEnded")
                _sessionEnded.TrySetResult();

            if (holdOn is not null && eventId.Name == holdOn && _held.TrySetResult())
            {
                // Held on this thread, synchronously, because the session logs synchronously from where it
                // stands. The bound is a failure bound: the test always lets it go, in its cleanup at the
                // latest.
                _proceed.Task.Wait(SignalTimeout);
            }
        }

        internal sealed record Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
    }
}
