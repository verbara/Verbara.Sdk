using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.TestInfrastructure;
using Verbara.Sdk.TestInfrastructure.Containers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Sessions;

/// <summary>
/// The session reconciliation sweep against a real Asterisk, in a host shaped as a consumer builds it
/// (<c>AddVerbara</c> + <c>AddVerbaraSessions</c>), with dialing and ringing timeouts far shorter than the call rings.
/// </summary>
/// <remarks>
/// <para>
/// The call is the one a short timeout ends wrongly: it rings for 8 s, is answered, and talks for 5 s. Its channels are
/// up the whole time, so Asterisk lists them at every sweep; the timeouts (3 s) only say when the sweep may ask.
/// </para>
/// <para>
/// The container is this class's alone, with a dialplan written for the call: the shared functional configuration
/// loads its PJSIP endpoints from a database this container does not have. The assembly runs its test classes one at
/// a time, so the session counters this class reads move only for its call.
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
public sealed class ReconciliationSweepTests : FunctionalTestBase, IClassFixture<ReconciliationSweepTests.SweepAsterisk>
{
    private const string SessionsMeter = "Verbara.Sdk.Sessions";

    /// <summary>A hang bound for the call: it lasts 13 s from the originate.</summary>
    private static readonly TimeSpan CallBound = TimeSpan.FromSeconds(60);

    /// <summary>A hang bound for the host's start and stop.</summary>
    private static readonly TimeSpan HostBound = TimeSpan.FromSeconds(30);

    private readonly SweepAsterisk _asterisk;
    private readonly ITestOutputHelper _output;

    public ReconciliationSweepTests(SweepAsterisk asterisk, ITestOutputHelper output) : base(SessionsMeter)
    {
        _asterisk = asterisk;
        _output = output;
    }

    [Fact]
    public async Task Sweep_ShouldLeaveARingingCallToBeAnsweredAndEndItCompleted_WhenItRingsPastTheTimeoutsAndTalks()
    {
        _output.WriteLine(_asterisk.Version);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddVerbara(o =>
                {
                    o.Ami.Hostname = _asterisk.AmiHost;
                    o.Ami.Port = SweepAsterisk.AmiPort;
                    o.Ami.Username = SweepAsterisk.User;
                    o.Ami.Password = _asterisk.Secret;
                    o.AgiPort = 0;
                });
                services.AddVerbaraSessions(o =>
                {
                    o.DialingTimeout = TimeSpan.FromSeconds(3);
                    o.RingingTimeout = TimeSpan.FromSeconds(3);
                    o.ReconciliationInterval = TimeSpan.FromSeconds(1);
                });
            })
            .Build();
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        var endings = new ConcurrentQueue<CallEndedEvent>();
        var ended = new TaskCompletionSource<CallEndedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = manager.Events.Subscribe(new EndingObserver(endings, ended));
        await host.StartAsync().WaitAsync(HostBound);

        try
        {
            var countersBefore = Counters();
            await host.Services.GetRequiredService<IAmiConnection>().SendActionAsync(new OriginateAction
            {
                Channel = "Local/caller@sweep/n",
                Application = "Wait",
                Data = "30",
                IsAsync = true,
            });

            CallEndedEvent ending;
            try
            {
                ending = await ended.Task.WaitAsync(CallBound);
            }
            catch (TimeoutException)
            {
                Assert.Fail(string.Create(CultureInfo.InvariantCulture,
                    $"the call did not end within {CallBound}; sessions held: {string.Join("; ", manager.ActiveSessions.Select(Describe))}"));
                throw;
            }

            var session = manager.GetById(ending.SessionId);
            var counters = Counters().Since(countersBefore);
            _output.WriteLine(session is null ? "the ended session is not held" : Describe(session));
            _output.WriteLine(counters.ToString());

            new
            {
                State = session?.State,
                HasTalkTime = session?.TalkTime is not null,
                CallEndedEvents = endings.Count(e => e.SessionId == ending.SessionId),
                counters.TimedOut,
                counters.Orphaned,
                counters.Completed,
            }.Should().BeEquivalentTo(
                new
                {
                    State = (CallSessionState?)CallSessionState.Completed,
                    HasTalkTime = true,
                    CallEndedEvents = 1,
                    TimedOut = 0L,
                    Orphaned = 0L,
                    Completed = 1L,
                },
                "the call's channels were up at every sweep, so Asterisk listed them and the sweep ended nothing; the "
                + "call was answered, talked and hung up normally, so it ends completed, once, with its talk time. "
                + $"Session: {(session is null ? "not held" : Describe(session))}");
        }
        finally
        {
            await host.StopAsync().WaitAsync(HostBound);
        }
    }

    private CounterReading Counters() => new(
        MetricsCapture.Get("sessions.completed"),
        MetricsCapture.Get("sessions.failed"),
        MetricsCapture.Get("sessions.timed_out"),
        MetricsCapture.Get("sessions.orphaned"));

    private static string Describe(CallSession session) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{session.SessionId} {session.State} cause={session.Metadata.GetValueOrDefault("cause") ?? "-"} " +
            $"talk={session.TalkTime?.ToString() ?? "-"} legs={session.Participants.Count} " +
            $"events=[{string.Join(", ", session.Events.Select(e => e.Type))}]");

    private sealed record CounterReading(long Completed, long Failed, long TimedOut, long Orphaned)
    {
        public CounterReading Since(CounterReading earlier) => new(
            Completed - earlier.Completed, Failed - earlier.Failed, TimedOut - earlier.TimedOut, Orphaned - earlier.Orphaned);
    }

    private sealed class EndingObserver(
        ConcurrentQueue<CallEndedEvent> endings,
        TaskCompletionSource<CallEndedEvent> first) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is not CallEndedEvent ending)
                return;

            endings.Enqueue(ending);
            first.TrySetResult(ending);
        }

        public void OnError(Exception error)
        {
            // The manager's subject never faults; nothing to record.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to record.
        }
    }

    // ── The Asterisk of this class's own ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An Asterisk container, its network and its configuration, for this class alone. Both are named
    /// <c>{prefix}sweep-{run}</c>: the prefix is <c>verbara-</c>, or <c>VERBARA_FUNCTIONAL_DOCKER_PREFIX</c> when it is
    /// set, so a machine shared by several runs can tell its own resources apart.
    /// </summary>
    public sealed class SweepAsterisk : IAsyncLifetime
    {
        public const int AmiPort = 5038;
        public const string User = "sweep";

        private string? _configDirectory;

        /// <summary>The AMI user's secret, generated for this run and written only to its own configuration.</summary>
        public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public string AmiHost => (Container ?? throw NotStarted()).NetworkAddress;

        /// <summary>What <c>core show version</c> printed in the container.</summary>
        public string Version { get; private set; } = "";

        // Released by DisposeAsync, which xUnit calls through IAsyncLifetime.
        private INetwork? Network { get; set; }

        private AsteriskContainer? Container { get; set; }

        public async Task InitializeAsync()
        {
            var prefix = Environment.GetEnvironmentVariable("VERBARA_FUNCTIONAL_DOCKER_PREFIX") ?? "verbara-";
            var name = prefix + "sweep-" + Guid.NewGuid().ToString("N")[..8];
            _configDirectory = WriteConfiguration(Path.Join(Path.GetTempPath(), name), Secret);

            var image = await AsteriskContainer.CreateImageAsync();
            Network = new NetworkBuilder().WithName(name).Build();
            await Network.CreateAsync();
            Container = new AsteriskContainer(Network, image, _configDirectory, name, publishPorts: false);
            await Container.StartAsync();

            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            // The CLI command itself waits for the end of the start; it fails only while no Asterisk runs to take it.
            while ((await Container.ExecAsync(["asterisk", "-rx", "core waitfullybooted"], ready.Token)).ExitCode != 0)
                ready.Token.ThrowIfCancellationRequested();
            Version = (await Container.ExecAsync(["asterisk", "-rx", "core show version"], ready.Token)).Stdout.Trim();
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
        /// Writes this class's own configuration: the functional <c>asterisk.conf</c>, no realtime and no database, an
        /// AMI user with the run's secret, and the call's dialplan.
        /// </summary>
        /// <remarks>
        /// The originate's <c>Local/caller@sweep/n</c> dials <c>Local/callee@sweep/n</c>; the callee rings for 8 s, answers
        /// and talks for 5 s, then hangs up, which ends every leg. The <c>/n</c> keeps Asterisk from optimising the local
        /// pairs away, so all four legs share the originate's linked id and form one call.
        /// </remarks>
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
            Write(directory, "extensions.conf", """
                [general]

                [sweep]
                exten => caller,1,NoOp(The caller's leg dials the callee)
                 same => n,Dial(Local/callee@sweep/n,30)
                 same => n,Hangup()

                exten => callee,1,NoOp(The callee rings 8 s, answers and talks 5 s)
                 same => n,Ringing()
                 same => n,Wait(8)
                 same => n,Answer()
                 same => n,Wait(5)
                 same => n,Hangup()
                """);
            Write(directory, "manager.conf", $"""
                [general]
                enabled = yes
                bindaddr = 0.0.0.0
                port = {AmiPort}

                [{User}]
                secret = {secret}
                read = system,call,agent,user,config,originate,reporting,command,dtmf,cdr
                write = system,call,agent,user,config,originate,command,reporting,dtmf
                deny = 0.0.0.0/0.0.0.0
                permit = 0.0.0.0/0.0.0.0
                """);
            return directory;
        }

        private static void Write(string directory, string file, string text) =>
            File.WriteAllText(Path.Join(directory, file), text + "\n");
    }
}
