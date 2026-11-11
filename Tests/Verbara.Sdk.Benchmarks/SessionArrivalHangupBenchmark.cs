using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// The session manager's two per-call hot paths, driven the way a server delivers them: a call's arrival (a first leg
/// announced through the server's channel table, which the manager turns into a new call and announces) and its
/// hangup (that leg removed, which ends the call). Each is measured with one server attached to the manager and with
/// four, the calls spread over the servers in turn, so a cost that grows with the attachments shows as the gap between
/// the two rows.
/// <para>
/// The <c>Arrival</c> and <c>Hangup</c> rows run over a connection that is not the SDK's, so the manager reads no loss
/// mark for the notifications it raises. The <c>Arrival_AmiConnection</c> and <c>Hangup_AmiConnection</c> rows run the
/// same calls over servers built on the SDK's <see cref="AmiConnection"/> (never connected, its socket never opened),
/// so every notification reads its server's loss mark the way a notification raised outside an event's delivery does.
/// </para>
/// <para>
/// Every iteration starts from a fresh manager and fresh servers, so the indexes and the ended-call queue do not grow
/// across iterations; the hangup iteration first opens the calls it then ends. One operation is one call. The store
/// keeps nothing, so the figures are the manager's and Live's own work, not a store's. Not part of the CI baseline: a
/// before/after comparison on one machine.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The manager and servers of each iteration are disposed in [IterationCleanup].")]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 30, invocationCount: 1)]
public class SessionArrivalHangupBenchmark
{
    private const int CallsPerIteration = 50_000;

    private string[] _uniqueIds = null!;
    private string[] _channelNames = null!;
    private CallSessionManager _manager = null!;
    private VerbaraServer[] _servers = null!;
    private AmiConnection[] _amiConnections = [];

    [Params(1, 4)]
    public int Servers { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _uniqueIds = new string[CallsPerIteration];
        _channelNames = new string[CallsPerIteration];
        for (var i = 0; i < CallsPerIteration; i++)
        {
            _uniqueIds[i] = "1700000000." + i.ToString(CultureInfo.InvariantCulture);
            _channelNames[i] = "PJSIP/trunk-" + i.ToString("D8", CultureInfo.InvariantCulture);
        }
    }

    [IterationSetup(Target = nameof(Arrival))]
    public void SetupArrival() => Build(overAmiConnection: false);

    [IterationSetup(Target = nameof(Hangup))]
    public void SetupHangup()
    {
        Build(overAmiConnection: false);
        ArriveAll();
    }

    [IterationSetup(Target = nameof(Arrival_AmiConnection))]
    public void SetupArrivalOverAmiConnection() => Build(overAmiConnection: true);

    [IterationSetup(Target = nameof(Hangup_AmiConnection))]
    public void SetupHangupOverAmiConnection()
    {
        Build(overAmiConnection: true);
        ArriveAll();
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var server in _servers)
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var connection in _amiConnections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _amiConnections = [];
    }

    [Benchmark(OperationsPerInvoke = CallsPerIteration)]
    public void Arrival() => ArriveAll();

    [Benchmark(OperationsPerInvoke = CallsPerIteration)]
    public void Hangup() => HangupAll();

    [Benchmark(OperationsPerInvoke = CallsPerIteration)]
    public void Arrival_AmiConnection() => ArriveAll();

    [Benchmark(OperationsPerInvoke = CallsPerIteration)]
    public void Hangup_AmiConnection() => HangupAll();

    private void HangupAll()
    {
        for (var i = 0; i < CallsPerIteration; i++)
            _servers[i % _servers.Length].Channels.OnHangup(_uniqueIds[i]);
    }

    private void ArriveAll()
    {
        for (var i = 0; i < CallsPerIteration; i++)
        {
            _servers[i % _servers.Length].Channels.OnNewChannel(_uniqueIds[i], _channelNames[i], ChannelState.Up,
                context: "from-trunk", linkedId: _uniqueIds[i]);
        }
    }

    private void Build(bool overAmiConnection)
    {
        _manager = new CallSessionManager(Options.Create(new SessionOptions()),
            NullLogger<CallSessionManager>.Instance, new KeepNothingStore());
        _servers = new VerbaraServer[Servers];
        _amiConnections = overAmiConnection ? new AmiConnection[Servers] : [];
        for (var s = 0; s < Servers; s++)
        {
            IAmiConnection connection;
            if (overAmiConnection)
            {
                _amiConnections[s] = new AmiConnection(Options.Create(new AmiConnectionOptions()),
                    new NeverOpenedSocketFactory(), NullLogger<AmiConnection>.Instance);
                connection = _amiConnections[s];
            }
            else
            {
                connection = new IdleAmiConnection();
            }

            _servers[s] = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
            _manager.AttachToServer(_servers[s], "s" + s.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A store that keeps nothing, so a save costs the manager's call and nothing else.</summary>
    private sealed class KeepNothingStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }

    /// <summary>
    /// The socket factory of an <see cref="AmiConnection"/> that is never connected: the rows never connect it, so a
    /// socket asked for is a fault of the benchmark.
    /// </summary>
    private sealed class NeverOpenedSocketFactory : ISocketConnectionFactory
    {
        public ISocketConnection Create() =>
            throw new NotSupportedException("The benchmark never connects its AmiConnection.");

        public ISocketConnection FromStream(Stream stream) =>
            throw new NotSupportedException("The benchmark never connects its AmiConnection.");
    }

    /// <summary>
    /// A connection that is never connected and carries nothing: the server is only a channel table here, and every
    /// leg is announced through it directly.
    /// </summary>
    private sealed class IdleAmiConnection : IAmiConnection
    {
        public AmiConnectionState State => AmiConnectionState.Disconnected;

        public string? AsteriskVersion => "22.0.0";

        public event Func<ManagerEvent, ValueTask>? OnEvent
        {
            add { }
            remove { }
        }

        public event Action? Reconnected
        {
            add { }
            remove { }
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action,
            CancellationToken cancellationToken = default) where TResponse : ManagerResponse =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => Empty(cancellationToken);

        public IDisposable Subscribe(IObserver<ManagerEvent> observer) => NoSubscription.Instance;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<ManagerEvent> Empty(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private sealed class NoSubscription : IDisposable
        {
            public static readonly NoSubscription Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
