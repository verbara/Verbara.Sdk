using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk;
using Verbara.Sdk.Agi.Diagnostics;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Agi.Server;

internal static partial class FastAgiServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[AGI] Server started: port={Port}")]
    public static partial void ServerStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AGI] Server stopped")]
    public static partial void ServerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGI] Connection accepted: remote={RemoteEndpoint}")]
    public static partial void ConnectionAccepted(ILogger logger, string? remoteEndpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGI] Script executing: script={Script} channel={Channel}")]
    public static partial void ScriptExecuting(ILogger logger, string? script, string? channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AGI] No script mapped: script={Script}")]
    public static partial void NoScriptMapped(ILogger logger, string? script);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AGI] Connection error")]
    public static partial void ConnectionError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AGI] Accept failed — the server stays bound and accepts again after a backoff")]
    public static partial void AcceptLoopFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Async FastAGI TCP server. Accepts connections from Asterisk,
/// parses AGI requests, maps them to scripts and executes them.
/// </summary>
public sealed class FastAgiServer : IAgiServer
{
    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    private readonly IMappingStrategy _mappingStrategy;
    private readonly ILogger<FastAgiServer> _logger;
    private readonly TimeProvider _timeProvider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _state = (int)AgiServerState.Stopped;

    public int Port { get; }
    public AgiServerState State => (AgiServerState)Volatile.Read(ref _state);
    public bool IsRunning => State == AgiServerState.Listening;

    private void SetState(AgiServerState newState) =>
        Interlocked.Exchange(ref _state, (int)newState);

    /// <summary>Maximum time allowed for a single AGI connection/script execution. Default: 5 minutes.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Replaces the server's accept when set. Settable by tests (via InternalsVisibleTo), so a test
    /// can make accepts fail on demand instead of exhausting file descriptors to get a failure.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    public FastAgiServer(int port, IMappingStrategy mappingStrategy, ILogger<FastAgiServer> logger)
        : this(port, mappingStrategy, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance whose accept backoff waits on <paramref name="timeProvider"/>, so a
    /// test can drive that wait with a fake clock instead of sitting out a real one.
    /// </summary>
    internal FastAgiServer(
        int port,
        IMappingStrategy mappingStrategy,
        ILogger<FastAgiServer> logger,
        TimeProvider timeProvider)
    {
        Port = port;
        _mappingStrategy = mappingStrategy;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (State == AgiServerState.Faulted)
            throw new InvalidOperationException("Cannot start from Faulted state. Call StopAsync first.");

        SetState(AgiServerState.Starting);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, Port);

        try
        {
            _listener.Start();
            SetState(AgiServerState.Listening);
        }
        catch
        {
            SetState(AgiServerState.Faulted);
            throw;
        }

        FastAgiServerLog.ServerStarted(_logger, Port);

        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var backoff = InitialAcceptBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await AcceptAsync(ct);
                backoff = InitialAcceptBackoff;
                client.NoDelay = true;

                var endpoint = client.Client.RemoteEndPoint?.ToString();
                FastAgiServerLog.ConnectionAccepted(_logger, endpoint);

                // Handle each connection concurrently
                _ = HandleConnectionAsync(client, ct);
                continue;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown. `ct` is _cts.Token, cancelled by StopAsync (and so by
                // DisposeAsync), and the pending accept ended with it. Nothing is meant to be
                // accepted after that, so the loop ends rather than backing off.
                break;
            }
            catch (ObjectDisposedException)
            {
                // Listener stopped — the Windows shape of that same stop: Stop() disposes the
                // underlying socket under a pending accept and the accept surfaces the disposal.
                // NOT reached on Linux, where Stop() on a pending accept raises
                // SocketException(OperationAborted) and the filtered catch below takes it instead;
                // this block is live on Windows, not dead code.
                break;
            }
            catch (SocketException) when (!IsRunning)
            {
                // The Linux shape of the stop: StopAsync writes Stopping as its FIRST statement,
                // before it calls _listener.Stop(), so an accept aborted by that Stop() always
                // arrives with IsRunning (State == Listening, read through Volatile.Read) already
                // false. IsRunning is the discriminator and not `ct`, because StopAsync cancels _cts
                // only after Stop() returns — a filter on ct.IsCancellationRequested would race that
                // ordering and report the stop as a failure.
                break;
            }
            catch (SocketException ex)
            {
                // An accept that failed while the server is still meant to be running: EMFILE/ENFILE
                // under a Native AOT process that never raised its descriptor limit, ENOBUFS, or a
                // connection aborted in the backlog. Ending the loop here would leave IsRunning
                // reporting true over a socket still in LISTEN, and AgiHealthCheck goes on reporting
                // Healthy off that state — Asterisk would keep completing handshakes nobody ever
                // reads. So it is reported at Error and the loop keeps accepting.
                FastAgiServerLog.AcceptLoopFailed(_logger, ex);
            }

            // A failure that persists fails every accept at once, and without a pause this loop would
            // spin and log without bound. The wait doubles with each consecutive failure up to the
            // cap, and a successful accept above starts it over.
            //
            // This await does a second job that is easy to miss, and removing the wait breaks it:
            // StartAsync starts this loop by calling it, not through Task.Run, so the loop runs on the
            // caller's thread until its first suspension point. In normal operation that is the accept
            // itself, which yields at once. On the failure path this delay is the only one — without it
            // a persistent failure spins INSIDE StartAsync instead of behind it, and StartAsync never
            // returns. Measured: the mutation that deletes this wait pins a test host at 98% CPU rather
            // than going red, which in CI reads as a job timeout instead of a failed test. If this wait
            // is ever moved or removed, start the loop with Task.Run first.
            try
            {
                await Task.Delay(backoff, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                // Same stop path as above, caught while waiting out a backoff rather than while
                // accepting: _cts was cancelled by StopAsync. The loop ends.
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
        }
    }

    private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) =>
        AcceptOverride?.Invoke(ct) ?? _listener!.AcceptTcpClientAsync(ct);

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (ConnectionTimeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(ConnectionTimeout);
        }

        var connectionCt = timeoutCts.Token;

        AgiMetrics.ConnectionsAccepted.Add(1);

        await using var conn = PipelineSocketConnection.FromStream(client.GetStream());

        var sw = Stopwatch.GetTimestamp();
        System.Diagnostics.Activity? activity = null;
        try
        {
            var reader = new FastAgiReader(conn.Input);
            var writer = new FastAgiWriter(conn.Output);

            // Read AGI request headers
            var request = await reader.ReadRequestAsync(connectionCt);

            activity = AgiActivitySource.StartScript(request.Script, request.Channel);
            FastAgiServerLog.ScriptExecuting(_logger, request.Script, request.Channel);

            // Map request to script
            var script = _mappingStrategy.Resolve(request);
            if (script is null)
            {
                AgiMetrics.ScriptsNotFound.Add(1);
                FastAgiServerLog.NoScriptMapped(_logger, request.Script);
                AgiActivitySource.SetResult(activity, AgiScriptResult.NotFound);
                return;
            }

            // Create channel and execute script
            var channel = new AgiChannel(writer, reader, _logger);
            await script.ExecuteAsync(channel, request, connectionCt);

            AgiMetrics.ScriptsExecuted.Add(1);
            AgiActivitySource.SetResult(activity, AgiScriptResult.Completed);
        }
        catch (AgiHangupException)
        {
            AgiMetrics.Hangups.Add(1);
            AgiActivitySource.SetResult(activity, AgiScriptResult.Hangup);
        }
        catch (OperationCanceledException)
        {
            AgiActivitySource.SetResult(activity, AgiScriptResult.Timeout);
        }
        catch (Exception ex)
        {
            AgiMetrics.ScriptsFailed.Add(1);
            FastAgiServerLog.ConnectionError(_logger, ex);
            AgiActivitySource.SetResult(activity, AgiScriptResult.Failed, ex.Message);
        }
        finally
        {
            AgiMetrics.ScriptDurationMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
            activity?.Dispose();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(AgiServerState.Stopping);

        _listener?.Stop();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        SetState(AgiServerState.Stopped);
        FastAgiServerLog.ServerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        var s = State;
        if (s is AgiServerState.Starting or AgiServerState.Listening or AgiServerState.Stopping)
            await StopAsync();
        _cts?.Dispose();
    }
}
