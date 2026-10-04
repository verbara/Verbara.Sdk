using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Agi.Tests.TestSupport;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Agi.Tests.Server;

/// <summary>
/// When a FastAGI connection carries a request <see cref="FastAgiServer"/> serves. A request is complete
/// at the blank line that ends the AGI environment block, as Asterisk writes it; a connection whose
/// stream ends before that line — a TCP liveness probe, a load balancer's health check, a port scanner
/// that connects and closes — carries no request, and the server records no script outcome for it.
/// </summary>
/// <remarks>
/// <para>
/// Every cell drives a real server on a loopback port with a burst of <see cref="N"/> peers of one
/// shape, and counts what the server emitted through four independent channels: the
/// <c>Verbara.Sdk.Agi</c> meter (by instrument name), the spans of the <c>Verbara.Sdk.Agi</c> activity
/// source (by <c>agi.result</c> and <c>agi.script</c>), the server's log (by level and event name), and
/// the script's own entry counter.
/// </para>
/// <para>
/// The fence is <c>agi.script.duration</c>: the server records it once per handled connection, in the
/// handler's <c>finally</c>, after everything else the handler records except the span's end. Spans are
/// therefore counted when they start, and their result tag is read after the fence; the tag is set
/// before the duration is recorded. Nothing here waits on a clock: <see cref="SignalTimeout"/> only
/// bounds the fence.
/// </para>
/// <para>
/// The instruments are process-wide, so the class runs in <see cref="AgiMetricsGroup"/>.
/// </para>
/// </remarks>
[Collection(AgiMetricsGroup.Name)]
public sealed class FastAgiRequestAdmissionTests
{
    /// <summary>Peers per cell.</summary>
    private const int N = 200;

    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The script name every strategy maps.</summary>
    private const string ScriptName = "hello";

    /// <summary>The event name of the Debug line the server writes for a connection with no request.</summary>
    private const string ClosedBeforeRequest = "ClosedBeforeRequest";

    /// <summary>Three header lines naming the mapped script, and no blank line after them.</summary>
    private const string TruncatedHeaders =
        "agi_network: yes\nagi_network_script: hello\nagi_channel: SIP/test\n";

    public static TheoryData<string, string> StreamEndsBeforeTheBlankLine()
    {
        var data = new TheoryData<string, string>();
        foreach (var strategy in Strategies)
        {
            foreach (var peer in TruncatingPeers)
                data.Add(strategy, peer);
        }

        return data;
    }

    public static TheoryData<string> EveryStrategy() => new(Strategies);

    private static readonly string[] Strategies =
        ["simple", "typename", "composite", "catchall-noio", "catchall-answer"];

    private static readonly string[] TruncatingPeers = ["rst-empty", "fin-empty", "rst-partial", "fin-partial"];

    // ------------------------------------------------- a stream that ends before the blank line

    [Theory]
    [MemberData(nameof(StreamEndsBeforeTheBlankLine))]
    public async Task HandleConnectionAsync_ShouldServeNothingAndLogOneDebugLine_WhenTheStreamEndsBeforeTheBlankLine(
        string strategy,
        string peer)
    {
        // Arrange
        using var counters = new AgiCounters();
        using var spans = new SpanCapture();
        var logger = new CountingLogger();
        var script = new CountingScript(answer: strategy == "catchall-answer");
        var server = new FastAgiServer(0, CreateStrategy(strategy, script), logger)
        {
            ConnectionTimeout = TimeSpan.FromSeconds(30),
        };
        var fence = counters.WhenRecorded(AgiCounters.ScriptDuration, N);

        await server.StartAsync();
        try
        {
            // Act
            await RunPeersAsync(server.BoundPort, peer);
            await fence.WaitAsync(SignalTimeout);

            // Assert
            var seen = Describe(counters, spans, logger, script);
            using (new AssertionScope())
            {
                counters.Get(AgiCounters.ScriptsNotFound).Should().Be(0, "a connection with no request names no script; seen: {0}", seen);
                counters.Get(AgiCounters.ScriptsExecuted).Should().Be(0, "no request, so no script ran; seen: {0}", seen);
                counters.Failed.Should().Be(0, "no request, so no script failed; seen: {0}", seen);
                counters.Get(AgiCounters.Hangups).Should().Be(0, "no request, so no script was hung up on; seen: {0}", seen);
                logger.Count(LogLevel.Warning).Should().Be(0, "a probe is not a mapping gap; seen: {0}", seen);
                logger.Count(LogLevel.Error).Should().Be(0, "a probe is not a connection error; seen: {0}", seen);
                spans.Total.Should().Be(0, "no request, so no script span; seen: {0}", seen);
                script.Entries.Should().Be(0, "the script is never entered for a connection with no request; seen: {0}", seen);
                logger.Count(LogLevel.Debug, ClosedBeforeRequest).Should().Be(
                    N, "each connection that ends before its request logs one Debug line; seen: {0}", seen);
                counters.Accepted.Should().Be(N, "every connection was accepted; seen: {0}", seen);
                server.IsRunning.Should().BeTrue("connections with no request do not stop the server");
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ------------------------------------------------------- a complete request: unchanged

    [Theory]
    [MemberData(nameof(EveryStrategy))]
    public async Task HandleConnectionAsync_ShouldServeEveryRequestAsBefore_WhenTheRequestEndsWithItsBlankLine(string strategy)
    {
        // Arrange
        using var counters = new AgiCounters();
        using var spans = new SpanCapture();
        var logger = new CountingLogger();
        var script = new CountingScript(answer: strategy == "catchall-answer");
        var server = new FastAgiServer(0, CreateStrategy(strategy, script), logger)
        {
            ConnectionTimeout = TimeSpan.FromSeconds(30),
        };
        var fence = counters.WhenRecorded(AgiCounters.ScriptDuration, N);

        await server.StartAsync();
        try
        {
            // Act
            await Task.WhenAll(Enumerable.Range(0, N).Select(i => WellFormedPeerAsync(server.BoundPort, ScriptName, i)));
            await fence.WaitAsync(SignalTimeout);

            // Assert
            var seen = Describe(counters, spans, logger, script);
            using (new AssertionScope())
            {
                counters.Get(AgiCounters.ScriptsExecuted).Should().Be(N, "every complete request is served; seen: {0}", seen);
                script.Entries.Should().Be(N, "the script runs once per request; seen: {0}", seen);
                script.EntriesFor(ScriptName).Should().Be(N, "every request named the script; seen: {0}", seen);
                counters.Get(AgiCounters.ScriptsNotFound).Should().Be(0, "seen: {0}", seen);
                counters.Failed.Should().Be(0, "seen: {0}", seen);
                counters.Get(AgiCounters.Hangups).Should().Be(0, "seen: {0}", seen);
                logger.Count(LogLevel.Warning).Should().Be(0, "seen: {0}", seen);
                logger.Count(LogLevel.Error).Should().Be(0, "seen: {0}", seen);
                spans.Total.Should().Be(N, "one span per request; seen: {0}", seen);
                spans.Count("Completed", ScriptName).Should().Be(N, "every span ends Completed for the script; seen: {0}", seen);
                logger.Count(LogLevel.Debug, ClosedBeforeRequest).Should().Be(
                    0, "a complete request did not close before its request; seen: {0}", seen);
                counters.Accepted.Should().Be(N, "seen: {0}", seen);
                server.IsRunning.Should().BeTrue();
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldReportTheScriptAsUnmapped_WhenACompleteRequestNamesAnUnregisteredScript()
    {
        // Arrange
        using var counters = new AgiCounters();
        using var spans = new SpanCapture();
        var logger = new CountingLogger();
        var script = new CountingScript(answer: false);
        var server = new FastAgiServer(0, CreateStrategy("simple", script), logger)
        {
            ConnectionTimeout = TimeSpan.FromSeconds(30),
        };
        var fence = counters.WhenRecorded(AgiCounters.ScriptDuration, 1);

        await server.StartAsync();
        try
        {
            // Act
            await WellFormedPeerAsync(server.BoundPort, "other", 0);
            await fence.WaitAsync(SignalTimeout);

            // Assert
            var seen = Describe(counters, spans, logger, script);
            using (new AssertionScope())
            {
                counters.Get(AgiCounters.ScriptsNotFound).Should().Be(1, "a request for an unmapped script is a mapping gap; seen: {0}", seen);
                logger.Count(LogLevel.Warning, "NoScriptMapped").Should().Be(1, "seen: {0}", seen);
                logger.Count(LogLevel.Debug, ClosedBeforeRequest).Should().Be(0, "seen: {0}", seen);
                spans.Count("NotFound", "other").Should().Be(1, "seen: {0}", seen);
                script.Entries.Should().Be(0, "seen: {0}", seen);
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------------------ peers

    private static async Task RunPeersAsync(int port, string peer)
    {
        var partial = peer.EndsWith("-partial", StringComparison.Ordinal);
        var reset = peer.StartsWith("rst-", StringComparison.Ordinal);
        var headers = Encoding.ASCII.GetBytes(TruncatedHeaders);

        // Every peer connects before any of them sends or closes: the burst shape.
        var connected = 0;
        var allConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => PeerAsync()));

        async Task PeerAsync()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
            }
            finally
            {
                // Counted even when the connect fails, so the other peers are released and the failure surfaces.
                if (Interlocked.Increment(ref connected) == N)
                    allConnected.TrySetResult();
            }

            await allConnected.Task.WaitAsync(SignalTimeout);

            if (partial)
                await socket.SendAsync(headers, SocketFlags.None);

            if (reset)
            {
                socket.LingerState = new LingerOption(true, 0);
                socket.Close();
                return;
            }

            // A FIN peer reads until the server closes its side, then closes its own.
            socket.Shutdown(SocketShutdown.Send);
            await DrainAsync(socket);
        }
    }

    private static async Task DrainAsync(Socket socket)
    {
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(SignalTimeout);
        try
        {
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token) > 0)
            {
                // Whatever the server writes (an ANSWER from a script) is read and not answered.
            }
        }
        catch (SocketException)
        {
            // The server reset its side instead of closing it; the fence decides what that means.
        }
    }

    private static async Task WellFormedPeerAsync(int port, string scriptName, int index)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var request =
            $"agi_network: yes\nagi_network_script: {scriptName}\nagi_channel: SIP/test-{index:D4}\nagi_uniqueid: 1700000000.{index}\n\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var cts = new CancellationTokenSource(SignalTimeout);
        while (await reader.ReadLineAsync(cts.Token) is not null)
            await stream.WriteAsync("200 result=0\n"u8.ToArray(), cts.Token);
    }

    // ------------------------------------------------------------------------------- strategies

    private static IMappingStrategy CreateStrategy(string strategy, IAgiScript script) => strategy switch
    {
        "simple" => Simple(script),
        "typename" => TypeName(script),
        "composite" => new CompositeMappingStrategy(Simple(script), TypeName(script)),
        "catchall-noio" or "catchall-answer" => new CatchAllStrategy(script),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "unknown strategy"),
    };

    private static SimpleMappingStrategy Simple(IAgiScript script)
    {
        var strategy = new SimpleMappingStrategy();
        strategy.Add(ScriptName, script);
        return strategy;
    }

    private static TypeNameMappingStrategy TypeName(IAgiScript script)
    {
        var strategy = new TypeNameMappingStrategy();
        strategy.Register(ScriptName, () => script);
        return strategy;
    }

    /// <summary>A consumer's strategy that returns its script for any request.</summary>
    private sealed class CatchAllStrategy(IAgiScript script) : IMappingStrategy
    {
        public IAgiScript? Resolve(IAgiRequest request) => script;
    }

    // ---------------------------------------------------------------------------- counting sinks

    private static string Describe(AgiCounters counters, SpanCapture spans, CountingLogger logger, CountingScript script) =>
        $"accepted={counters.Accepted} executed={counters.Get(AgiCounters.ScriptsExecuted)} " +
        $"not_found={counters.Get(AgiCounters.ScriptsNotFound)} failed={counters.Failed} " +
        $"hangups={counters.Get(AgiCounters.Hangups)} duration_records={counters.Records(AgiCounters.ScriptDuration)} " +
        $"entries={script.Entries} (null Script: {script.EntriesFor(null)}) " +
        $"spans=[{spans.Describe()}] log=[{logger.Describe()}]";

    /// <summary>A script that counts its entries by the request's <c>Script</c>, and optionally sends ANSWER.</summary>
    private sealed class CountingScript(bool answer) : IAgiScript
    {
        private const string NullKey = "(null)";
        private readonly ConcurrentDictionary<string, int> _entries = new(StringComparer.Ordinal);

        public int Entries => _entries.Values.Sum();

        public int EntriesFor(string? script) => _entries.GetValueOrDefault(script ?? NullKey);

        public async ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            _entries.AddOrUpdate(request.Script ?? NullKey, 1, (_, n) => n + 1);
            if (answer)
                await channel.AnswerAsync(cancellationToken);
        }
    }

    /// <summary>Counts the server's log entries by level and event name.</summary>
    private sealed class CountingLogger : ILogger<FastAgiServer>
    {
        private readonly ConcurrentDictionary<(LogLevel Level, string Name), int> _counts = new();

        public int Count(LogLevel level) => _counts.Where(kv => kv.Key.Level == level).Sum(kv => kv.Value);

        public int Count(LogLevel level, string eventName) => _counts.GetValueOrDefault((level, eventName));

        public string Describe() => string.Join(
            ", ",
            _counts.OrderBy(kv => kv.Key.Level).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key.Level}:{kv.Key.Name}={kv.Value}"));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _counts.AddOrUpdate((logLevel, eventId.Name ?? "(unnamed)"), 1, (_, n) => n + 1);
    }

    /// <summary>
    /// Captures every span the <c>Verbara.Sdk.Agi</c> source starts while it is alive. A span is kept
    /// when it starts, so it is counted before the handler records its duration; its <c>agi.result</c>
    /// is read when the capture is described or counted, after the fence.
    /// </summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _started = new();
        private readonly ActivityListener _listener;

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Verbara.Sdk.Agi",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = _started.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public int Total => _started.Count;

        public int Count(string result, string? script) => _started.Count(activity =>
            string.Equals(activity.GetTagItem("agi.result") as string, result, StringComparison.Ordinal)
            && string.Equals(activity.GetTagItem("agi.script") as string, script, StringComparison.Ordinal));

        public string Describe() => string.Join(
            ", ",
            _started
                .GroupBy(activity => $"{activity.GetTagItem("agi.result") as string ?? "none"}|{activity.GetTagItem("agi.script") as string ?? "null"}")
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}"));

        public void Dispose() => _listener.Dispose();
    }
}
