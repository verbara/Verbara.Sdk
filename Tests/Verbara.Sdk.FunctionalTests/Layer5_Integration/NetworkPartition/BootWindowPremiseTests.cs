using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.TestInfrastructure;
using Verbara.Sdk.TestInfrastructure.Containers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.NetworkPartition;

/// <summary>
/// What Asterisk does with an AMI session that logs in while it is still loading its modules, measured on an Asterisk
/// of this class's own that it restarts, and what a live-state reload that logs in there loads.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk accepts the AMI login before app_queue has registered <c>QueueStatus</c>, and refuses the action until it
/// has, with <c>Response: Error</c> and a <c>Message</c> beginning <c>Invalid/unknown command</c>. It reports the end
/// of its start with <c>FullyBooted</c>, to AMI users with <c>system</c> in their read permissions and to no one
/// else. Over raw AMI sessions logged in right after a container restart, on Asterisk 20.20.1, 22.9.0 and 23.4.1, the
/// login was accepted 80–131 ms before <c>QueueStatus</c> worked, <c>FullyBooted</c> came 17–28 ms after it did, and a
/// user without <c>system</c> received none. These tests hold those premises against whichever version the image
/// builds, and hold a reload that logs in inside that window to loading the queue as Asterisk has it.
/// </para>
/// <para>
/// <b>Landing in the window.</b> Asterisk is restarted with <c>core restart now</c>, which re-executes it inside the
/// same container, so its address does not change. Every client reaches it through <see cref="AmiRelay"/>: the relay
/// accepts the client at once, dials Asterisk every <see cref="AmiRelay.DialInterval"/>, and forwards from the moment
/// Asterisk sends its AMI banner, so the client's login follows Asterisk's first accept by one dial at most. The relay
/// dials the container's own address, with no published port, so a successful connect is Asterisk listening and not a
/// Docker proxy; this needs a Linux Docker host, where the host reaches a container's network directly.
/// </para>
/// <para>
/// <b>No vacuous green.</b> A restart whose first <c>QueueStatus</c> is already answered did not land in the window.
/// Each test tries <see cref="Attempts"/> restarts, and fails as inconclusive when none lands there.
/// </para>
/// <para>
/// The container is this class's alone, never the shared functional fixture's, because the tests restart Asterisk.
/// Its configuration is written per run, with a queue that has a static member, an agent, an AMI user with
/// <c>system</c> and one without, and a secret generated for the run.
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
public sealed class BootWindowPremiseTests : IClassFixture<BootWindowPremiseTests.OwnAsterisk>
{
    /// <summary>How many restarts a test tries before it reports that none landed in the window.</summary>
    private const int Attempts = 5;

    private const string StateLoaded = "[LIVE] State loaded";

    /// <summary>A hang bound for one restart and everything that follows it.</summary>
    private static readonly TimeSpan AttemptBound = TimeSpan.FromSeconds(90);

    private readonly OwnAsterisk _asterisk;
    private readonly ITestOutputHelper _output;

    public BootWindowPremiseTests(OwnAsterisk asterisk, ITestOutputHelper output)
    {
        _asterisk = asterisk;
        _output = output;
    }

    [Fact]
    public async Task QueueStatus_ShouldBeRefusedUntilAsteriskReportsFullyBootedToSystemUsersOnly_WhenTheLoginLandsInTheBootWindow()
    {
        _output.WriteLine(_asterisk.Version);
        await using var relay = new AmiRelay(_asterisk.AmiHost, OwnAsterisk.AmiPort);
        PremiseAttempt? landed = null;
        for (var number = 1; number <= Attempts && landed is null; number++)
        {
            using var bound = new CancellationTokenSource(AttemptBound);
            await _asterisk.WaitFullyBootedAsync(bound.Token);
            var attempt = await RunPremiseAttemptAsync(relay, number, bound.Token);
            _output.WriteLine(attempt.ToString());
            if (attempt.LandedInTheWindow)
                landed = attempt;
        }

        if (landed is null)
        {
            Assert.Fail(string.Create(CultureInfo.InvariantCulture,
            $"inconclusive: none of {Attempts} restarts landed a login in Asterisk's boot window, so the premise was not observed"));
            return;
        }

        using (new AssertionScope())
        {
            landed.FirstAnswer.Should().Be("Error", "Asterisk refuses QueueStatus until app_queue has registered it");
            landed.FirstMessage.Should().StartWith("Invalid/unknown command",
                "the refusal reads exactly like the one for a module that is not loaded at all");
            landed.FullyBootedSequence.Should().NotBeNull("the user with system receives FullyBooted once Asterisk has started");
            landed.SecondAnswer.Should().Be("Success", "once Asterisk has reported FullyBooted, QueueStatus is answered");
            landed.QueueStrategy.Should().Be(OwnAsterisk.QueueStrategy, "the queue is reported with its strategy");
            landed.QueueMembers.Should().Equal([OwnAsterisk.MemberLocation], "the queue is reported with its static member");
            landed.NoSystemSawFullyBooted.Should().BeFalse(
                "a user without system in its read permissions receives no FullyBooted, although it was logged in before Asterisk sent it");
        }
    }

    [Fact]
    public async Task Reload_ShouldLoadTheQueueWithItsStrategyAndMember_WhenItLogsInWhileAsteriskLoadsItsModules()
    {
        _output.WriteLine(_asterisk.Version);
        using (var ready = new CancellationTokenSource(AttemptBound))
            await _asterisk.WaitFullyBootedAsync(ready.Token);

        await using var relay = new AmiRelay(_asterisk.AmiHost, OwnAsterisk.AmiPort);
        var serverLog = new LineSignals<VerbaraServer>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "127.0.0.1",
            Port = relay.Port,
            Username = OwnAsterisk.SystemUser,
            Password = _asterisk.Secret,
            EnableHeartbeat = false,
            AutoReconnect = true,
            // The reconnect reaches the relay while Asterisk is still down, and the relay holds it there until Asterisk
            // listens again: the reconnect's backoff does not decide when the login lands.
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(100),
            ConnectionTimeout = TimeSpan.FromSeconds(60),
            DefaultResponseTimeout = TimeSpan.FromSeconds(10),
            DefaultEventTimeout = TimeSpan.FromSeconds(10),
        }), new PipelineSocketConnectionFactory(), NullLogger<AmiConnection>.Instance);
        await using var server = new VerbaraServer(connection, serverLog);

        using (var start = new CancellationTokenSource(AttemptBound))
        {
            await connection.ConnectAsync(start.Token);
            await server.StartAsync(start.Token);
        }

        var atStart = LoadedState.Of(server);
        _output.WriteLine($"start: {atStart}");
        ReloadAttempt? landed = null;
        for (var number = 1; number <= Attempts && landed is null; number++)
        {
            using var bound = new CancellationTokenSource(AttemptBound);
            await _asterisk.WaitFullyBootedAsync(bound.Token);
            var loadsBefore = serverLog.Count(StateLoaded);
            var sessionsBefore = relay.SessionCount;
            await relay.ArmAsync(bound.Token);
            await _asterisk.RestartAsync(bound.Token);
            await serverLog.Logged(StateLoaded, loadsBefore + 1).WaitAsync(bound.Token);
            var reloaded = LoadedState.Of(server);
            var refused = relay.SessionsSince(sessionsBefore)
                .Any(session => session.Received("Invalid/unknown command: QueueStatus"));
            var attempt = new ReloadAttempt(number, refused, reloaded);
            _output.WriteLine(attempt.ToString());
            if (refused)
                landed = attempt;
        }

        using (new AssertionScope())
        {
            atStart.QueueStrategy.Should().Be(OwnAsterisk.QueueStrategy, "positive control: the start ran on a started Asterisk");
            atStart.QueueMembers.Should().Be(1, "positive control: the start loaded the queue's member");
        }

        if (landed is null)
        {
            Assert.Fail(string.Create(CultureInfo.InvariantCulture,
            $"inconclusive: none of {Attempts} reloads logged in while Asterisk still refused QueueStatus, so the window was not observed"));
            return;
        }

        using (new AssertionScope())
        {
            landed.Loaded.QueueStrategy.Should().Be(OwnAsterisk.QueueStrategy,
                "a refusal from an Asterisk that is still starting is not an empty table: the reload loads the queue, with its strategy");
            landed.Loaded.QueueMembers.Should().Be(1, "the reload loads the queue's static member");
            landed.Loaded.AgentLoaded.Should().BeTrue("the reload loads the agent");
        }
    }

    /// <summary>
    /// One restart, observed from two raw AMI sessions that log in as soon as Asterisk accepts them: the user with
    /// <c>system</c> asks for the queues at once, waits for <c>FullyBooted</c> if it was refused, and asks again; the user
    /// without <c>system</c> answers a <c>Ping</c> once the other has seen the report, which bounds what it could have
    /// received before.
    /// </summary>
    private async Task<PremiseAttempt> RunPremiseAttemptAsync(AmiRelay relay, int number, CancellationToken cancellationToken)
    {
        var order = new ArrivalOrder();
        await relay.ArmAsync(cancellationToken);
        await using var system = await RawAmiSession.OpenAsync(relay.Port, order, cancellationToken);
        await using var noSystem = await RawAmiSession.OpenAsync(relay.Port, order, cancellationToken);
        await _asterisk.RestartAsync(cancellationToken);

        try
        {
            // Both log in as soon as Asterisk accepts them, a round trip apart, before anything else is asked.
            await system.LoginAsync(OwnAsterisk.SystemUser, _asterisk.Secret, cancellationToken);
            var noSystemLoggedIn = await noSystem.LoginAsync(OwnAsterisk.NoSystemUser, _asterisk.Secret, cancellationToken);
            var first = await system.AskAsync("QueueStatus", "first", cancellationToken);
            if (!string.Equals(first.Response.Get("Response"), "Error", StringComparison.OrdinalIgnoreCase))
                return PremiseAttempt.Missed(number, first, "the first QueueStatus was already answered");

            var fullyBooted = await system.WaitForEventAsync("FullyBooted", cancellationToken);
            var second = await system.AskAsync("QueueStatus", "second", cancellationToken);
            await noSystem.AskAsync("Ping", "after-fully-booted", cancellationToken);

            return new PremiseAttempt(
                number,
                first.Response.Get("Response"),
                first.Response.Get("Message"),
                noSystemLoggedIn.Sequence,
                fullyBooted.Sequence,
                noSystem.Seen.Any(message => message.IsEvent("FullyBooted")),
                second.Response.Get("Response"),
                second.Events.FirstOrDefault(evt => evt.IsEvent("QueueParams") && evt.Get("Queue") == OwnAsterisk.QueueName)
                    ?.Get("Strategy"),
                [.. second.Events.Where(evt => evt.IsEvent("QueueMember") && evt.Get("Queue") == OwnAsterisk.QueueName)
                    .Select(evt => evt.Get("Location") ?? evt.Get("Interface") ?? "")],
                Failure: null);
        }
        catch (IOException ex)
        {
            // Asterisk ended a session mid-attempt, as it does when an action arrives while its module registers it.
            // That restart observed nothing; the next one is tried.
            return PremiseAttempt.Missed(number, first: null, ex.Message);
        }
    }

    /// <summary>What one restart showed on the raw sessions.</summary>
    private sealed record PremiseAttempt(
        int Number,
        string? FirstAnswer,
        string? FirstMessage,
        long? NoSystemLoginSequence,
        long? FullyBootedSequence,
        bool? NoSystemSawFullyBooted,
        string? SecondAnswer,
        string? QueueStrategy,
        IReadOnlyList<string> QueueMembers,
        string? Failure)
    {
        /// <summary>
        /// The first <c>QueueStatus</c> was refused, and the user without <c>system</c> was logged in before Asterisk
        /// sent <c>FullyBooted</c>, so both halves of the premise were observed inside the window.
        /// </summary>
        public bool LandedInTheWindow =>
            Failure is null
            && string.Equals(FirstAnswer, "Error", StringComparison.OrdinalIgnoreCase)
            && NoSystemLoginSequence < FullyBootedSequence;

        public static PremiseAttempt Missed(int number, Answer? first, string reason) =>
            new(number, first?.Response.Get("Response"), first?.Response.Get("Message"), null, null, null, null, null, [],
                reason);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"premise attempt {Number}: first QueueStatus {FirstAnswer} ({FirstMessage}); no-system login #{NoSystemLoginSequence}, FullyBooted #{FullyBootedSequence}; no-system saw FullyBooted: {NoSystemSawFullyBooted}; second QueueStatus {SecondAnswer}, strategy {QueueStrategy}, members [{string.Join(", ", QueueMembers)}]; landed: {LandedInTheWindow}{(Failure is null ? "" : "; " + Failure)}");
    }

    /// <summary>What one restart showed through the live server's reload.</summary>
    private sealed record ReloadAttempt(int Number, bool Refused, LoadedState Loaded)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"reload attempt {Number}: QueueStatus refused on the reload's session: {Refused}; reloaded {Loaded}");
    }

    /// <summary>The queue and the agent as the live server holds them.</summary>
    private sealed record LoadedState(string? QueueStrategy, int QueueMembers, bool AgentLoaded)
    {
        public static LoadedState Of(VerbaraServer server)
        {
            var queue = server.Queues.GetByName(OwnAsterisk.QueueName);
            return new LoadedState(queue?.Strategy, queue?.MemberCount ?? 0, server.Agents.GetById(OwnAsterisk.AgentId) is not null);
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"queue strategy {QueueStrategy ?? "(no queue)"}, {QueueMembers} member(s), agent loaded: {AgentLoaded}");
    }

    // ── The Asterisk of this class's own ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An Asterisk container, its network and its configuration, for this class alone. The container and the network are
    /// named <c>{prefix}boot-window-{run}</c>: the prefix is <c>verbara-</c>, or <c>VERBARA_FUNCTIONAL_DOCKER_PREFIX</c>
    /// when it is set, so a machine shared by several runs can tell its own resources apart.
    /// </summary>
    public sealed class OwnAsterisk : IAsyncLifetime
    {
        public const int AmiPort = 5038;
        public const string SystemUser = "bootwindow-system";
        public const string NoSystemUser = "bootwindow-nosystem";
        public const string QueueName = "bootwindow";
        public const string QueueStrategy = "ringall";
        public const string MemberLocation = "Local/agent@boot-window/n";
        public const string AgentId = "1001";

        private string? _configDirectory;

        /// <summary>The AMI secret of both users, generated for this run and written only to its own configuration.</summary>
        public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public string AmiHost => (Container ?? throw NotStarted()).NetworkAddress;

        /// <summary>What <c>asterisk -V</c> printed in the container.</summary>
        public string Version { get; private set; } = "";

        // Released by DisposeAsync, which xUnit calls through IAsyncLifetime.
        private INetwork? Network { get; set; }

        private AsteriskContainer? Container { get; set; }

        public async Task InitializeAsync()
        {
            var prefix = Environment.GetEnvironmentVariable("VERBARA_FUNCTIONAL_DOCKER_PREFIX") ?? "verbara-";
            var name = prefix + "boot-window-" + Guid.NewGuid().ToString("N")[..8];
            _configDirectory = WriteConfiguration(Path.Join(Path.GetTempPath(), name), Secret);

            var image = await AsteriskContainer.CreateImageAsync();
            Network = new NetworkBuilder().WithName(name).Build();
            await Network.CreateAsync();
            Container = new AsteriskContainer(Network, image, _configDirectory, name, publishPorts: false);
            await Container.StartAsync();

            using var ready = new CancellationTokenSource(AttemptBound);
            await WaitFullyBootedAsync(ready.Token);
            Version = (await Container.ExecAsync(["asterisk", "-V"], ready.Token)).Stdout.Trim();
        }

        /// <summary>Re-executes Asterisk inside its container: every AMI session ends, and Asterisk starts again.</summary>
        public Task RestartAsync(CancellationToken cancellationToken) =>
            (Container ?? throw NotStarted()).ExecAsync(["asterisk", "-rx", "core restart now"], cancellationToken);

        /// <summary>Returns once Asterisk has finished starting.</summary>
        public async Task WaitFullyBootedAsync(CancellationToken cancellationToken)
        {
            var container = Container ?? throw NotStarted();
            // The CLI command itself waits for the end of the start. It fails only while no Asterisk is running to
            // take it, and each call is a round trip into the container, so the loop asks again at once.
            while ((await container.ExecAsync(["asterisk", "-rx", "core waitfullybooted"], cancellationToken)).ExitCode != 0)
                cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task DisposeAsync()
        {
            if (Container is not null)
                await Container.DisposeAsync();

            if (Network is not null)
                await Network.DisposeAsync();

            if (_configDirectory is not null && Directory.Exists(_configDirectory))
                Directory.Delete(_configDirectory, recursive: true);
        }

        private static InvalidOperationException NotStarted() => new("The container has not been started.");

        /// <summary>
        /// Writes this class's own configuration: the functional <c>asterisk.conf</c>, and everything else here. No
        /// realtime and no database, so nothing Asterisk loads waits on a server this container does not have.
        /// </summary>
        private static string WriteConfiguration(string directory, string secret)
        {
            Directory.CreateDirectory(directory);
            File.Copy(Path.Join(DockerPaths.AsteriskConfig, "asterisk.conf"), Path.Join(directory, "asterisk.conf"));
            Write(directory, "logger.conf", """
                [general]

                [logfiles]
                console => notice,warning,error
                """);
            Write(directory, "modules.conf", """
                [modules]
                autoload = yes
                noload = chan_sip.so
                noload = chan_skinny.so
                noload = chan_mgcp.so
                noload = res_config_pgsql.so
                noload = res_config_odbc.so
                noload = res_odbc.so
                noload = res_config_ldap.so
                noload = res_config_sqlite3.so
                """);
            Write(directory, "manager.conf", $"""
                [general]
                enabled = yes
                bindaddr = 0.0.0.0
                port = {AmiPort}

                [{SystemUser}]
                secret = {secret}
                read = system,call,agent,user,config,originate,reporting,command,dtmf,cdr
                write = system,call,agent,user,config,originate,command,reporting,dtmf
                deny = 0.0.0.0/0.0.0.0
                permit = 0.0.0.0/0.0.0.0

                [{NoSystemUser}]
                secret = {secret}
                read = call,agent,user,originate,reporting,dtmf,cdr
                write = call,agent,user,originate,reporting,dtmf
                deny = 0.0.0.0/0.0.0.0
                permit = 0.0.0.0/0.0.0.0
                """);
            Write(directory, "queues.conf", $"""
                [general]
                persistentmembers = no

                [{QueueName}]
                strategy = {QueueStrategy}
                timeout = 15
                retry = 1
                joinempty = yes
                leavewhenempty = no
                member => {MemberLocation},0,Agent One,Custom:bootwindow1
                """);
            Write(directory, "agents.conf", $"""
                [general]

                [{AgentId}]
                fullname = Agent {AgentId}
                """);
            Write(directory, "extensions.conf", """
                [general]

                [boot-window]
                exten => agent,1,Answer()
                 same => n,Hangup()
                """);
            return directory;
        }

        private static void Write(string directory, string file, string text) =>
            File.WriteAllText(Path.Join(directory, file), text + "\n");
    }

    // ── The relay ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts AMI clients on <c>127.0.0.1</c> at once, and connects each to Asterisk the moment Asterisk sends its AMI
    /// banner, dialling every <see cref="DialInterval"/> until it does. Once <see cref="ArmAsync"/> has been called, a
    /// client is connected only after the Asterisk that was running then has gone away, so it lands on the Asterisk
    /// that starts next, and never on one that is shutting down.
    /// </summary>
    private sealed class AmiRelay : IAsyncDisposable
    {
        /// <summary>
        /// How often the relay dials an Asterisk that is not listening yet. It is the relay's cadence, not a wait on any
        /// outcome: it bounds how late after Asterisk's first accept a client's login can land.
        /// </summary>
        public static readonly TimeSpan DialInterval = TimeSpan.FromMilliseconds(2);

        private const string Banner = "Asterisk Call Manager/";

        private readonly string _host;
        private readonly int _port;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopped = new();
        private readonly Lock _gate = new();
        private readonly List<RelaySession> _sessions = [];
        private readonly List<Task> _running = [];
        private readonly Task _accepting;

        // Completed while no restart is expected; otherwise completes once the Asterisk running at the arm has gone.
        private TaskCompletionSource _gone = Completed();

        public AmiRelay(string host, int port)
        {
            _host = host;
            _port = port;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync(_stopped.Token);
        }

        public int Port { get; }

        public int SessionCount
        {
            get
            {
                lock (_gate)
                {
                    return _sessions.Count;
                }
            }
        }

        public IReadOnlyList<RelaySession> SessionsSince(int count)
        {
            lock (_gate)
            {
                return [.. _sessions.Skip(count)];
            }
        }

        /// <summary>
        /// Opens a session of the relay's own on the Asterisk that is running now, and holds it: that session ends when
        /// this Asterisk goes away, and every client accepted from now on is connected only after that.
        /// </summary>
        public async Task ArmAsync(CancellationToken cancellationToken)
        {
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sentinel = new TcpClient { NoDelay = true };
            try
            {
                await sentinel.ConnectAsync(_host, _port, cancellationToken);
                if (await ReadBannerAsync(sentinel.GetStream(), cancellationToken) is null)
                    throw new IOException("The running Asterisk sent no AMI banner to the relay's own session.");
            }
            catch
            {
                // The arm failed and the caller gets the failure: nothing holds this socket any more.
                sentinel.Dispose();
                throw;
            }

            lock (_gate)
            {
                _gone = gone;
                _running.Add(WatchAsync(sentinel, gone, _stopped.Token));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopped.CancelAsync();
            _listener.Stop();
            Task[] running;
            lock (_gate)
            {
                running = [.. _running, _accepting];
            }

            await Task.WhenAll(running);
            _stopped.Dispose();
        }

        private static TaskCompletionSource Completed()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completed.SetResult();
            return completed;
        }

        private async Task AcceptAsync(CancellationToken stopped)
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(stopped);
                    client.NoDelay = true;
                    lock (_gate)
                    {
                        var session = new RelaySession();
                        _sessions.Add(session);
                        _running.Add(RelayAsync(client, session, _gone.Task, stopped));
                    }
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
                // The relay was disposed: it accepts no more clients.
            }
            catch (SocketException) when (stopped.IsCancellationRequested)
            {
                // The listener was stopped under a pending accept, by the relay's disposal.
            }
            catch (ObjectDisposedException) when (stopped.IsCancellationRequested)
            {
                // The listener was stopped under a pending accept, by the relay's disposal.
            }
        }

        private async Task RelayAsync(TcpClient client, RelaySession session, Task gone, CancellationToken stopped)
        {
            using (client)
            {
                try
                {
                    await gone.WaitAsync(stopped);
                    using var cadence = new PeriodicTimer(DialInterval);
                    while (true)
                    {
                        using (var asterisk = new TcpClient { NoDelay = true })
                        {
                            if (await TryDialAsync(asterisk, stopped) is { } banner)
                            {
                                await ForwardAsync(client, asterisk, banner, session, stopped);
                                return;
                            }
                        }

                        await cadence.WaitForNextTickAsync(stopped);
                    }
                }
                catch (OperationCanceledException) when (stopped.IsCancellationRequested)
                {
                    // The relay was disposed while this client waited for Asterisk; the sockets close as their scopes end.
                }
                catch (IOException)
                {
                    // The client went away before Asterisk answered; the sockets close as their scopes end.
                }
            }
        }

        /// <summary>
        /// One dial: the banner when Asterisk answered with it, <see langword="null"/> when it is not listening yet or
        /// dropped the connection first, so that the next dial follows at the relay's cadence.
        /// </summary>
        private async Task<byte[]?> TryDialAsync(TcpClient asterisk, CancellationToken stopped)
        {
            try
            {
                await asterisk.ConnectAsync(_host, _port, stopped);
                return await ReadBannerAsync(asterisk.GetStream(), stopped);
            }
            catch (SocketException)
            {
                // Asterisk is not listening yet; the next dial follows at the relay's cadence.
                return null;
            }
            catch (IOException)
            {
                // Asterisk dropped the connection before its banner; the next dial follows at the relay's cadence.
                return null;
            }
        }

        /// <summary>Hands the banner to the client, then forwards both ways until either side ends.</summary>
        private static async Task ForwardAsync(TcpClient client, TcpClient asterisk, byte[] banner, RelaySession session,
            CancellationToken stopped)
        {
            var toClient = client.GetStream();
            await toClient.WriteAsync(banner, stopped);
            session.Record(banner);
            var fromAsterisk = PumpAsync(asterisk.GetStream(), toClient, session, stopped);
            var fromClient = PumpAsync(toClient, asterisk.GetStream(), record: null, stopped);
            await Task.WhenAny(fromAsterisk, fromClient);
            // Either side ended: closing both ends the other pump, which never throws.
            client.Dispose();
            asterisk.Dispose();
            await Task.WhenAll(fromAsterisk, fromClient);
        }

        /// <summary>The banner line, read up to its line break; <see langword="null"/> when the connection ended first.</summary>
        private static async Task<byte[]?> ReadBannerAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var read = new List<byte>();
            var buffer = new byte[256];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    return null;

                read.AddRange(buffer.AsSpan(0, count));
                var text = Encoding.ASCII.GetString([.. read]);
                if (text.Contains("\r\n", StringComparison.Ordinal))
                {
                    return text.StartsWith(Banner, StringComparison.Ordinal)
                        ? [.. read]
                        : throw new IOException("The AMI port answered something other than Asterisk's banner: " + text.Trim());
                }
            }
        }

        /// <summary>Holds the relay's own session until the Asterisk it is connected to goes away.</summary>
        private static async Task WatchAsync(TcpClient sentinel, TaskCompletionSource gone, CancellationToken stopped)
        {
            using (sentinel)
            {
                var buffer = new byte[1024];
                try
                {
                    while (await sentinel.GetStream().ReadAsync(buffer, stopped) > 0)
                    {
                        // Whatever this Asterisk still sends is not what the relay waits for.
                    }
                }
                catch (OperationCanceledException) when (stopped.IsCancellationRequested)
                {
                    // The relay was disposed while it held the session.
                }
                catch (IOException)
                {
                    // A reset ends the session as surely as a close.
                }
                finally
                {
                    gone.TrySetResult();
                }
            }
        }

        /// <summary>Copies one direction until it ends. It never throws: an ended connection is how a relay session ends.</summary>
        private static async Task PumpAsync(NetworkStream from, NetworkStream to, RelaySession? record, CancellationToken stopped)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (await from.ReadAsync(buffer, stopped) is var count and > 0)
                {
                    record?.Record(buffer.AsSpan(0, count));
                    await to.WriteAsync(buffer.AsMemory(0, count), stopped);
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
                // The relay was disposed.
            }
            catch (IOException)
            {
                // One side closed or reset the connection: that ends this direction.
            }
            catch (ObjectDisposedException)
            {
                // The relay closed this side because the other direction ended.
            }
        }
    }

    /// <summary>One client's session through the relay: everything Asterisk sent it.</summary>
    private sealed class RelaySession
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _received = new();

        public void Record(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                _received.Append(Encoding.UTF8.GetString(bytes));
            }
        }

        public bool Received(string text)
        {
            lock (_gate)
            {
                return _received.ToString().Contains(text, StringComparison.Ordinal);
            }
        }
    }

    // ── A raw AMI session ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Numbers every message as it arrives, on every session of one attempt, so their order can be compared.</summary>
    private sealed class ArrivalOrder
    {
        private long _last;

        public long Next() => Interlocked.Increment(ref _last);
    }

    /// <summary>One AMI message: its headers, and its place in <see cref="ArrivalOrder"/>.</summary>
    private sealed record AmiMessage(long Sequence, IReadOnlyDictionary<string, string> Fields)
    {
        public string? Get(string key) => Fields.TryGetValue(key, out var value) ? value : null;

        public bool IsEvent(string name) => string.Equals(Get("Event"), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An action's response, and the events of its list when it has one.</summary>
    private sealed record Answer(AmiMessage Response, IReadOnlyList<AmiMessage> Events);

    /// <summary>
    /// An AMI session with no SDK in between: it writes actions as text and reads every message Asterisk sends, in order.
    /// </summary>
    private sealed class RawAmiSession : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly ArrivalOrder _order;
        private readonly Channel<AmiMessage> _unread = Channel.CreateUnbounded<AmiMessage>();
        private readonly Lock _gate = new();
        private readonly List<AmiMessage> _seen = [];
        private readonly CancellationTokenSource _stopped = new();
        private readonly Task _reading;

        private RawAmiSession(TcpClient client, ArrivalOrder order)
        {
            _client = client;
            _order = order;
            _reading = ReadAsync(_stopped.Token);
        }

        /// <summary>Every message received so far, in order.</summary>
        public IReadOnlyList<AmiMessage> Seen
        {
            get
            {
                lock (_gate)
                {
                    return [.. _seen];
                }
            }
        }

        public static async Task<RawAmiSession> OpenAsync(int port, ArrivalOrder order, CancellationToken cancellationToken)
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return new RawAmiSession(client, order);
        }

        /// <summary>Logs in with the plain-text secret; returns Asterisk's answer. A refused login throws.</summary>
        public async Task<AmiMessage> LoginAsync(string user, string secret, CancellationToken cancellationToken)
        {
            var answer = await AskAsync("Login", "login", cancellationToken, ("Username", user), ("Secret", secret));
            return string.Equals(answer.Response.Get("Response"), "Success", StringComparison.OrdinalIgnoreCase)
                ? answer.Response
                : throw new InvalidOperationException($"Asterisk refused the login of {user}: {answer.Response.Get("Message")}");
        }

        /// <summary>
        /// Sends the action and reads until its answer is complete: the response, and when the response opens a list,
        /// every event up to the list's completion event. Messages for anything else are skipped, and kept in
        /// <see cref="Seen"/>.
        /// </summary>
        public async Task<Answer> AskAsync(string action, string actionId, CancellationToken cancellationToken,
            params (string Key, string Value)[] fields)
        {
            var text = new StringBuilder();
            text.Append("Action: ").Append(action).Append("\r\nActionID: ").Append(actionId).Append("\r\n");
            foreach (var (key, value) in fields)
                text.Append(key).Append(": ").Append(value).Append("\r\n");

            text.Append("\r\n");
            await _client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), cancellationToken);

            AmiMessage? response = null;
            var events = new List<AmiMessage>();
            while (true)
            {
                var message = await NextAsync(cancellationToken);
                if (!string.Equals(message.Get("ActionID"), actionId, StringComparison.Ordinal))
                    continue;

                if (message.Get("Response") is { } status)
                {
                    response = message;
                    var opensAList = string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(message.Get("EventList"), "start", StringComparison.OrdinalIgnoreCase);
                    if (!opensAList)
                        return new Answer(response, events);
                }
                else if (response is not null && message.Get("Event") is { } name)
                {
                    events.Add(message);
                    if (name.EndsWith("Complete", StringComparison.OrdinalIgnoreCase))
                        return new Answer(response, events);
                }
            }
        }

        /// <summary>The first event named <paramref name="name"/> this session received, already or from now on.</summary>
        public async Task<AmiMessage> WaitForEventAsync(string name, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (Seen.FirstOrDefault(message => message.IsEvent(name)) is { } found)
                    return found;

                await NextAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopped.CancelAsync();
            _client.Dispose();
            await _reading;
            _stopped.Dispose();
        }

        private async Task<AmiMessage> NextAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _unread.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException ex)
            {
                // The reader completes the channel when Asterisk ends the session.
                throw new IOException("Asterisk ended the session.", ex);
            }
        }

        private async Task ReadAsync(CancellationToken stopped)
        {
            var pending = new StringBuilder();
            var bannerRead = false;
            var buffer = new byte[16 * 1024];
            try
            {
                while (await _client.GetStream().ReadAsync(buffer, stopped) is var count and > 0)
                {
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    var text = pending.ToString();
                    var consumed = 0;
                    if (!bannerRead && text.IndexOf("\r\n", StringComparison.Ordinal) is var bannerEnd and >= 0)
                    {
                        consumed = bannerEnd + 2;
                        bannerRead = true;
                    }

                    while (bannerRead && text.IndexOf("\r\n\r\n", consumed, StringComparison.Ordinal) is var end and >= 0)
                    {
                        Receive(text[consumed..end]);
                        consumed = end + 4;
                    }

                    pending.Remove(0, consumed);
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
                // The session was disposed.
            }
            catch (IOException)
            {
                // Asterisk reset the session: it has ended, which the channel's completion reports.
            }
            catch (ObjectDisposedException)
            {
                // The session was disposed under a pending read.
            }
            finally
            {
                _unread.Writer.TryComplete();
            }
        }

        private void Receive(string block)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in block.Split("\r\n"))
            {
                var colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                    fields.TryAdd(line[..colon], line[(colon + 2)..]);
            }

            var message = new AmiMessage(_order.Next(), fields);
            lock (_gate)
            {
                _seen.Add(message);
            }

            _unread.Writer.TryWrite(message);
        }
    }

    // ── The live server's log, as signals ──────────────────────────────────────────────────────────────────────────

    /// <summary>A logger whose lines a test awaits: <see cref="Logged"/> completes once enough lines match.</summary>
    private sealed class LineSignals<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];
        private readonly List<(string Fragment, int Times, TaskCompletionSource Signal)> _waiters = [];

        /// <summary>How many lines containing <paramref name="fragment"/> have been logged.</summary>
        public int Count(string fragment)
        {
            lock (_gate)
            {
                return CountLocked(fragment);
            }
        }

        /// <summary>Completes once <paramref name="times"/> lines containing <paramref name="fragment"/> have been logged.</summary>
        public Task Logged(string fragment, int times)
        {
            lock (_gate)
            {
                if (CountLocked(fragment) >= times)
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((fragment, times, signal));
                return signal.Task;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            List<TaskCompletionSource> fired = [];
            lock (_gate)
            {
                _lines.Add(line);
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    var (fragment, times, signal) = _waiters[i];
                    if (CountLocked(fragment) >= times)
                    {
                        fired.Add(signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var signal in fired)
                signal.TrySetResult();
        }

        private int CountLocked(string fragment) =>
            _lines.Count(line => line.Contains(fragment, StringComparison.Ordinal));
    }
}
