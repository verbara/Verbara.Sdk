using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ari.Client;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Sessions;

/// <summary>
/// A real ARI snoop on a live call against a real Asterisk, in a host shaped as a consumer builds it
/// (<c>AddVerbara</c> + <c>AddVerbaraSessions</c>): the snoop is an Asterisk-internal tap, not a call.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk names the snoop itself (<c>Snoop/&lt;target uniqueid&gt;-&lt;n&gt;</c>) and gives it its linked id, so the
/// channel the session manager sees comes from the substrate, never from this code.
/// </para>
/// <para>
/// The container is this class's alone, with a dialplan written for the call and ARI enabled for the snoop's
/// application: the shared functional configuration loads its PJSIP endpoints from a database this container does not
/// have. The assembly runs its test classes one at a time, so the session counters this class reads move only for its
/// calls. Each test writes one <c>SNOOP-CELL</c> line with what it observed before it asserts anything.
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
public sealed class SnoopChannelSessionTests : FunctionalTestBase, IClassFixture<SnoopChannelSessionTests.SnoopAsterisk>
{
    private const string SessionsMeter = "Verbara.Sdk.Sessions";

    /// <summary>A hang bound for each step of the call (an event to arrive, the call to end).</summary>
    private static readonly TimeSpan StepBound = TimeSpan.FromSeconds(20);

    /// <summary>A hang bound for the host's start and stop.</summary>
    private static readonly TimeSpan HostBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the test lets the session manager act on a channel event it already received before reading what it
    /// holds. Asserting that something did not happen needs a window; this is it.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);

    private readonly SnoopAsterisk _asterisk;
    private readonly ITestOutputHelper _output;

    public SnoopChannelSessionTests(SnoopAsterisk asterisk, ITestOutputHelper output) : base(SessionsMeter)
    {
        _asterisk = asterisk;
        _output = output;
    }

    [Fact]
    public async Task Snoop_ShouldOpenNoSessionAndLeaveTheCallUnchanged_WhenARealAriSnoopTapsALiveCall()
    {
        var reading = await RunCallAsync(snoop: true);

        reading.SnoopName.Should().StartWith("Snoop/" + reading.TargetUniqueId + "-",
            "Asterisk names an ARI snoop after the channel it taps");
        new
        {
            reading.ActiveWhileSnoopUp,
            ParticipantsWhileSnoopUp = reading.ParticipantsWhileSnoopUp,
            reading.ActiveAfterSnoopHangup,
            reading.CallLiveAfterSnoopHangup,
            reading.Created,
            reading.Started,
            reading.Endings,
            reading.ActiveAtEnd,
        }.Should().BeEquivalentTo(
            new
            {
                ActiveWhileSnoopUp = 1,
                ParticipantsWhileSnoopUp = reading.ParticipantsBefore,
                ActiveAfterSnoopHangup = 1,
                CallLiveAfterSnoopHangup = true,
                Created = 1L,
                Started = 1,
                Endings = 1,
                ActiveAtEnd = 0,
            },
            "a snoop is a tap on the call, not a call: it opens no session, joins none and ends none, so the call is "
            + "the only session from its start to its single ending. " + reading);
    }

    [Fact]
    public async Task Call_ShouldOpenExactlyOneSession_WhenNoSnoopTapsIt()
    {
        var reading = await RunCallAsync(snoop: false);

        new
        {
            reading.ActiveWhileSnoopUp,
            reading.Created,
            reading.Started,
            reading.Endings,
            reading.ActiveAtEnd,
        }.Should().BeEquivalentTo(
            new { ActiveWhileSnoopUp = 1, Created = 1L, Started = 1, Endings = 1, ActiveAtEnd = 0 },
            "the same call with no snoop is one session, started and ended once (the control of the snoop test). "
            + reading);
    }

    /// <summary>
    /// Places the call, waits for its callee leg to answer, optionally snoops on that leg through ARI and hangs the
    /// snoop up, then hangs the call up and reads what the session manager held at each step.
    /// </summary>
    private async Task<Reading> RunCallAsync(bool snoop)
    {
        _output.WriteLine(_asterisk.Version);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddVerbara(o =>
                {
                    o.Ami.Hostname = _asterisk.Host;
                    o.Ami.Port = SnoopAsterisk.AmiPort;
                    o.Ami.Username = SnoopAsterisk.User;
                    o.Ami.Password = _asterisk.Secret;
                    o.AgiPort = 0;
                });
                services.AddVerbaraSessions();
            })
            .Build();
        var manager = host.Services.GetRequiredService<ICallSessionManager>();
        var notifications = new SessionNotifications();
        using var subscription = manager.Events.Subscribe(notifications);
        await host.StartAsync().WaitAsync(HostBound);

        await using var ari = new AriClient(
            Options.Create(new AriClientOptions
            {
                BaseUrl = $"http://{_asterisk.Host}:{SnoopAsterisk.AriPort}",
                Username = SnoopAsterisk.User,
                Password = _asterisk.Secret,
                Application = SnoopAsterisk.Application,
                AutoReconnect = false,
            }),
            NullLogger<AriClient>.Instance);
        await ari.ConnectAsync().AsTask().WaitAsync(HostBound);

        var ami = host.Services.GetRequiredService<IAmiConnection>();
        var channels = new ChannelEvents();
        using var amiSubscription = ami.Subscribe(channels);
        var call = "c" + Guid.NewGuid().ToString("N")[..8];
        ObservedChannel? target = null;
        string? tapId = null;

        try
        {
            var activeAtStart = manager.ActiveSessions.Count();
            var createdBefore = MetricsCapture.Get("sessions.created");
            await ami.SendActionAsync(new OriginateAction
            {
                Channel = $"Local/caller-{call}@snoop/n",
                Application = "Wait",
                Data = "60",
                IsAsync = true,
            });

            // The callee's answering half: Local/callee-<call>@snoop-<n>;2 runs Answer() then Wait(60).
            target = await channels.WaitForNewChannelAsync(
                c => c.Name.StartsWith($"Local/callee-{call}@snoop-", StringComparison.Ordinal)
                     && c.Name.EndsWith(";2", StringComparison.Ordinal),
                StepBound);
            // fence-allow: SETTLE — lets the session manager act on the Newchannel it already received before the reading; asserting a call did not open needs a window
            await Task.Delay(Settle);
            var session = manager.GetByLinkedId(target.LinkedId);
            var participantsBefore = Participants(session);

            ObservedChannel? snoopChannel = null;
            var activeWhileSnoopUp = manager.ActiveSessions.Count();
            var participantsWhileSnoopUp = participantsBefore;
            var activeAfterSnoopHangup = activeWhileSnoopUp;
            var callLiveAfterSnoopHangup = session is not null;
            var snoopEndedBy = "-";
            if (snoop)
            {
                var tap = await ari.Channels.SnoopAsync(target.UniqueId, SnoopAsterisk.Application, spy: "both");
                tapId = tap.Id;
                snoopChannel = await channels.WaitForNewChannelAsync(c => c.UniqueId == tap.Id, StepBound);
                // fence-allow: SETTLE — lets the session manager act on the snoop's Newchannel before the reading; asserting the snoop opened no session needs a window
                await Task.Delay(Settle);
                activeWhileSnoopUp = manager.ActiveSessions.Count();
                participantsWhileSnoopUp = Participants(manager.GetByLinkedId(target.LinkedId));
                _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"SNOOP-UP snoop={snoopChannel.Name} snoopLinkedid={snoopChannel.LinkedId} "
                    + $"target={target.Name} targetUniqueid={target.UniqueId} "
                    + $"targetLinkedid={target.LinkedId} activeWhileSnoopUp={activeWhileSnoopUp} "
                    + $"sessionsCreatedSoFar={MetricsCapture.Get("sessions.created") - createdBefore}"));

                // ARI's hang-up first; a snoop that outlives it is hung up over AMI, and the reading says which ended it.
                await ari.Channels.HangupAsync(tap.Id);
                if (await channels.TryWaitForHangupAsync(tap.Id, TimeSpan.FromSeconds(5)))
                {
                    snoopEndedBy = "ari";
                }
                else
                {
                    await ami.SendActionAsync(new HangupAction { Channel = snoopChannel.Name });
                    snoopEndedBy = await channels.TryWaitForHangupAsync(tap.Id, TimeSpan.FromSeconds(10)) ? "ami" : "none";
                }

                // fence-allow: SETTLE — lets the session manager act on the snoop's hang-up (if any) before the reading
                await Task.Delay(Settle);
                activeAfterSnoopHangup = manager.ActiveSessions.Count();
                callLiveAfterSnoopHangup = manager.ActiveSessions.Any(s => s.LinkedId == target.LinkedId);
            }

            await ari.Channels.HangupAsync(target.UniqueId);
            await channels.WaitForHangupAsync(target.UniqueId, StepBound);
            // "none" is set only on the snoop path, after tapId is.
            if (snoopEndedBy == "none")
                snoopEndedBy = await channels.TryWaitForHangupAsync(tapId!, TimeSpan.FromSeconds(10)) ? "with-call" : "never";

            var activeAtEnd = await WaitForNoActiveSessionAsync(manager, StepBound);

            var reading = new Reading(
                snoop ? "snoop" : "control",
                activeAtStart,
                target.Name,
                target.UniqueId,
                target.LinkedId,
                snoopChannel?.Name ?? "-",
                snoopChannel?.UniqueId ?? "-",
                snoopChannel?.LinkedId ?? "-",
                snoopChannel is not null && snoopChannel.LinkedId != target.LinkedId,
                snoopEndedBy,
                activeWhileSnoopUp,
                participantsBefore,
                participantsWhileSnoopUp,
                activeAfterSnoopHangup,
                callLiveAfterSnoopHangup,
                MetricsCapture.Get("sessions.created") - createdBefore,
                notifications.Started,
                notifications.Endings,
                activeAtEnd);
            _output.WriteLine("SNOOP-CELL " + reading);
            return reading;
        }
        finally
        {
            // A step that failed leaves its channels up; hang them up so the next test starts from an idle Asterisk.
            foreach (var id in new[] { tapId, target?.UniqueId }.OfType<string>())
            {
                try
                {
                    await ari.Channels.HangupAsync(id);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Already gone: the normal path hung it up.
                }
            }

            await WaitForNoActiveSessionAsync(manager, StepBound);
            await host.StopAsync().WaitAsync(HostBound);
        }
    }

    private static async Task<int> WaitForNoActiveSessionAsync(ICallSessionManager manager, TimeSpan bound)
    {
        using var timeout = new CancellationTokenSource(bound);
        while (manager.ActiveSessions.Any() && !timeout.IsCancellationRequested)
            // fence-allow: LOOP-DRIVER — polls the manager's active sessions under the caller's bound
            await Task.Delay(100, CancellationToken.None);

        return manager.ActiveSessions.Count();
    }

    private static string Participants(CallSession? session) =>
        session is null
            ? "none"
            : string.Join(",", session.Participants.Select(p => p.Channel).Order(StringComparer.Ordinal));

    /// <summary>What one call showed at each step; its text is the <c>SNOOP-CELL</c> line.</summary>
    private sealed record Reading(
        string Variant,
        int ActiveAtStart,
        string TargetName,
        string TargetUniqueId,
        string TargetLinkedId,
        string SnoopName,
        string SnoopUniqueId,
        string SnoopLinkedId,
        bool LinkedIdDiffers,
        string SnoopEndedBy,
        int ActiveWhileSnoopUp,
        string ParticipantsBefore,
        string ParticipantsWhileSnoopUp,
        int ActiveAfterSnoopHangup,
        bool CallLiveAfterSnoopHangup,
        long Created,
        int Started,
        int Endings,
        int ActiveAtEnd)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"variant={Variant} activeAtStart={ActiveAtStart} target={TargetName} targetUniqueid={TargetUniqueId} targetLinkedid={TargetLinkedId} "
            + $"snoop={SnoopName} snoopUniqueid={SnoopUniqueId} snoopLinkedid={SnoopLinkedId} "
            + $"linkedidDiffers={LinkedIdDiffers} snoopEndedBy={SnoopEndedBy} activeWhileSnoopUp={ActiveWhileSnoopUp} "
            + $"participantsBefore=[{ParticipantsBefore}] participantsWhileSnoopUp=[{ParticipantsWhileSnoopUp}] "
            + $"activeAfterSnoopHangup={ActiveAfterSnoopHangup} callLiveAfterSnoopHangup={CallLiveAfterSnoopHangup} "
            + $"sessionsCreated={Created} started={Started} endings={Endings} activeAtEnd={ActiveAtEnd}");
    }

    private sealed record ObservedChannel(string Name, string UniqueId, string LinkedId);

    /// <summary>The AMI channel arrivals and hangups of the run, as Asterisk wrote them.</summary>
    private sealed class ChannelEvents : IObserver<ManagerEvent>
    {
        private readonly ConcurrentQueue<ObservedChannel> _arrivals = new();
        private readonly ConcurrentDictionary<string, bool> _hangups = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _seen = new();

        public async Task<ObservedChannel> WaitForNewChannelAsync(Func<ObservedChannel, bool> match, TimeSpan bound)
        {
            using var timeout = new CancellationTokenSource(bound);
            while (!timeout.IsCancellationRequested)
            {
                var found = _arrivals.FirstOrDefault(match);
                if (found is not null)
                    return found;

                // fence-allow: LOOP-DRIVER — polls the recorded AMI arrivals under the caller's bound
                await Task.Delay(50, CancellationToken.None);
            }

            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"no matching Newchannel within {bound}; seen: {string.Join("; ", _arrivals.Select(a => a.Name))}"));
        }

        public async Task<bool> TryWaitForHangupAsync(string uniqueId, TimeSpan bound)
        {
            using var timeout = new CancellationTokenSource(bound);
            while (!_hangups.ContainsKey(uniqueId))
            {
                if (timeout.IsCancellationRequested)
                    return false;

                // fence-allow: LOOP-DRIVER — polls the recorded AMI hangups under the caller's bound
                await Task.Delay(50, CancellationToken.None);
            }

            return true;
        }

        public async Task WaitForHangupAsync(string uniqueId, TimeSpan bound)
        {
            using var timeout = new CancellationTokenSource(bound);
            while (!_hangups.ContainsKey(uniqueId))
            {
                if (timeout.IsCancellationRequested)
                    throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                        $"no Hangup for {uniqueId} within {bound}; its events: {string.Join("; ", _seen.Where(e => e.Contains(uniqueId, StringComparison.Ordinal)))}"));

                // fence-allow: LOOP-DRIVER — polls the recorded AMI hangups under the caller's bound
                await Task.Delay(50, CancellationToken.None);
            }
        }

        public void OnNext(ManagerEvent value)
        {
            if (value.UniqueId is not null)
                _seen.Enqueue(value.GetType().Name + " " + value.UniqueId);

            switch (value)
            {
                case NewChannelEvent arrival when arrival.Channel is not null && arrival.UniqueId is not null:
                    _arrivals.Enqueue(new ObservedChannel(arrival.Channel, arrival.UniqueId, arrival.Linkedid ?? arrival.UniqueId));
                    break;
                case HangupEvent hangup when hangup.UniqueId is not null:
                    _hangups[hangup.UniqueId] = true;
                    break;
            }
        }

        public void OnError(Exception error)
        {
            // The connection's stream reports its own faults; nothing to record.
        }

        public void OnCompleted()
        {
            // Completed when the connection is disposed; nothing to record.
        }
    }

    /// <summary>Counts the session manager's call starts and endings.</summary>
    private sealed class SessionNotifications : IObserver<SessionDomainEvent>
    {
        private int _started;
        private int _endings;

        public int Started => Volatile.Read(ref _started);

        public int Endings => Volatile.Read(ref _endings);

        public void OnNext(SessionDomainEvent value)
        {
            switch (value)
            {
                case CallStartedEvent:
                    Interlocked.Increment(ref _started);
                    break;
                case CallEndedEvent:
                    Interlocked.Increment(ref _endings);
                    break;
            }
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
    /// An Asterisk container, its network and its configuration, for this class alone, with AMI and ARI for one user
    /// whose secret is generated for the run. Both are named <c>{prefix}snoop-{run}</c>: the prefix is
    /// <c>verbara-</c>, or <c>VERBARA_FUNCTIONAL_DOCKER_PREFIX</c> when it is set.
    /// </summary>
    public sealed class SnoopAsterisk : IAsyncLifetime
    {
        public const int AmiPort = 5038;
        public const int AriPort = 8088;
        public const string User = "snoop";
        public const string Application = "snoop-cell";

        private string? _configDirectory;

        /// <summary>The AMI and ARI user's secret, generated for this run and written only to its own configuration.</summary>
        public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public string Host => (Container ?? throw NotStarted()).NetworkAddress;

        /// <summary>What <c>core show version</c> printed in the container.</summary>
        public string Version { get; private set; } = "";

        // Released by DisposeAsync, which xUnit calls through IAsyncLifetime.
        private INetwork? Network { get; set; }

        private AsteriskContainer? Container { get; set; }

        public async Task InitializeAsync()
        {
            var prefix = Environment.GetEnvironmentVariable("VERBARA_FUNCTIONAL_DOCKER_PREFIX") ?? "verbara-";
            var name = prefix + "snoop-" + Guid.NewGuid().ToString("N")[..8];
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
        /// Writes this class's own configuration: the functional <c>asterisk.conf</c>, no realtime and no database, AMI
        /// and ARI for the run's user, and the call's dialplan.
        /// </summary>
        /// <remarks>
        /// The originate's <c>Local/caller-&lt;call&gt;@snoop/n</c> dials <c>Local/callee-&lt;call&gt;@snoop/n</c>, whose
        /// second half answers and waits; the <c>/n</c> keeps Asterisk from optimising the local pairs away, so all four
        /// legs share the originate's linked id and form one call. The test hangs the callee up, which ends every leg.
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

                [snoop]
                exten => _caller-.,1,NoOp(The caller's leg dials the callee)
                 same => n,Dial(Local/callee-${EXTEN:7}@snoop/n,30)
                 same => n,Hangup()

                exten => _callee-.,1,NoOp(The callee answers and talks until the test hangs it up)
                 same => n,Answer()
                 same => n,Wait(60)
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
            Write(directory, "http.conf", $"""
                [general]
                enabled = yes
                bindaddr = 0.0.0.0
                bindport = {AriPort}
                """);
            Write(directory, "ari.conf", $"""
                [general]
                enabled = yes

                [{User}]
                type = user
                password = {secret}
                read_only = no
                """);
            return directory;
        }

        private static void Write(string directory, string file, string text) =>
            File.WriteAllText(Path.Join(directory, file), text + "\n");
    }
}
