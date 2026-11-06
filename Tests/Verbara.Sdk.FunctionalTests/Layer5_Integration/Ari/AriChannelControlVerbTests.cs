using System.Collections.Concurrent;
using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ari;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.TestInfrastructure;
using Verbara.Sdk.TestInfrastructure.Containers;
using FluentAssertions;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Ari;

/// <summary>
/// Channel hold, mute and unmute, and log-channel rotation, sent by the SDK to a real Asterisk: every call must be one
/// Asterisk accepts, because a verb or a query it rejects answers 405 or 400 whatever the channel.
/// </summary>
/// <remarks>
/// <para>
/// The call is two PJSIP legs on one Asterisk, through an endpoint looped back to itself: the A leg is originated into
/// Stasis, and its INVITE comes back in as the B leg, which the dialplan answers and also hands to Stasis. Both legs
/// are in the application, so the SDK acts on a live, answered PJSIP channel, the kind Asterisk was measured on.
/// </para>
/// <para>
/// Holding the A leg is seen on the B leg: with <c>moh_passthrough</c> the hold travels to the far leg as a re-INVITE,
/// and the B leg raises <c>ChannelHold</c>. Asterisk raises none on the held leg itself.
/// </para>
/// <para>
/// The container is this class's alone: the shared functional configuration has no PJSIP endpoint, logs only to the
/// console, and runs its Stasis channels as <c>Local/</c> halves.
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
public sealed class AriChannelControlVerbTests : FunctionalTestBase, IClassFixture<AriChannelControlVerbTests.VerbsAsterisk>
{
    private const string Application = "test-app";

    /// <summary>The event bound the other ARI functional tests use.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly VerbsAsterisk _asterisk;
    private readonly ITestOutputHelper _output;

    public AriChannelControlVerbTests(VerbsAsterisk asterisk, ITestOutputHelper output) : base("Verbara.Sdk.Ari")
    {
        _asterisk = asterisk;
        _output = output;
    }

    [Fact]
    public async Task ChannelControl_ShouldCompleteEveryCall_WhenSentToALiveAnsweredPjsipLeg()
    {
        _output.WriteLine(_asterisk.Version);

        // The rig: messages.log must be a file log channel of this Asterisk, or rotating it says nothing.
        var loggers = await _asterisk.ExecAsync(["asterisk", "-rx", "logger show channels"]);
        loggers.ExitCode.Should().Be(0, "the CLI must answer: {0}", loggers.Stderr);
        loggers.Stdout.Should().Contain("messages.log", "this class's logger.conf defines the file channel messages.log");

        await using var ari = AriClientFactory.Create(_asterisk.Address, VerbsAsterisk.AriPort, LoggerFactory, Application);
        var bLeg = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aLeg = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holds = new ConcurrentQueue<string>();
        var bHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = ari.Subscribe(new Observer(evt =>
        {
            switch (evt)
            {
                case StasisStartEvent { Channel: { } channel } start when start.Args is ["bside", ..]:
                    bLeg.TrySetResult(channel.Id);
                    break;
                case StasisStartEvent { Channel: { } channel }:
                    aLeg.TrySetResult(channel.Id);
                    break;
                case ChannelHoldEvent { Channel: { } channel }:
                    holds.Enqueue(channel.Id);
                    if (bLeg.Task.IsCompletedSuccessfully && channel.Id == bLeg.Task.Result)
                        bHold.TrySetResult();
                    break;
            }
        }));
        await ari.ConnectAsync();

        await using var ami = AmiConnectionFactory.Create(_asterisk.Address, VerbsAsterisk.AmiPort, LoggerFactory, o =>
        {
            o.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            o.AutoReconnect = false;
        });
        await ami.ConnectAsync();
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "PJSIP/1005@loop",
            Application = "Stasis",
            Data = Application,
            IsAsync = true,
        });

        var a = await aLeg.Task.WaitAsync(EventTimeout);
        var b = await bLeg.Task.WaitAsync(EventTimeout);
        _output.WriteLine($"A leg {a}, B leg {b}");

        try
        {
            var outcomes = new List<string>
            {
                await Outcome("hold A", () => ari.Channels.HoldAsync(a)),
                await HoldSeenOnB(bHold.Task, holds),
                await Outcome("mute A, no direction", () => ari.Channels.MuteAsync(a)),
                await Outcome("mute A, direction in", () => ari.Channels.MuteAsync(a, "in")),
                await Outcome("unmute A, direction in", () => ari.Channels.UnmuteAsync(a, "in")),
                await Outcome("unmute A, no direction", () => ari.Channels.UnmuteAsync(a)),
                await Outcome("rotate messages.log", () => ari.Asterisk.RotateLogChannelAsync("messages.log")),
            };
            foreach (var line in outcomes)
                _output.WriteLine(line);

            outcomes.Should().Equal(
                [
                    "hold A: ok",
                    "ChannelHold on B: ok",
                    "mute A, no direction: ok",
                    "mute A, direction in: ok",
                    "unmute A, direction in: ok",
                    "unmute A, no direction: ok",
                    "rotate messages.log: ok",
                ],
                "each call is sent with the verb and the query Asterisk accepts, on an answered PJSIP leg in Stasis");
        }
        finally
        {
            await BestEffort.AriAsync(() => ari.Channels.HangupAsync(a));
            await BestEffort.AriAsync(() => ari.Channels.HangupAsync(b));
        }
    }

    private static async Task<string> Outcome(string call, Func<ValueTask> act)
    {
        try
        {
            await act();
            return call + ": ok";
        }
        catch (AriException ex)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{call}: AriException {ex.StatusCode} {ex.Message}");
        }
    }

    private static async Task<string> HoldSeenOnB(Task bHold, ConcurrentQueue<string> holds)
    {
        try
        {
            await bHold.WaitAsync(EventTimeout);
            return "ChannelHold on B: ok";
        }
        catch (TimeoutException)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"ChannelHold on B: none within {EventTimeout} (ChannelHold seen on: [{string.Join(", ", holds)}])");
        }
    }

    private sealed class Observer(Action<AriEvent> onNext) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value) => onNext(value);

        public void OnError(Exception error)
        {
            // The client's subject never faults; nothing to record.
        }

        public void OnCompleted()
        {
            // Completed when the client is disposed; nothing to record.
        }
    }

    // ── The Asterisk of this class's own ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An Asterisk container, its network and its configuration, for this class alone. Both are named
    /// <c>{prefix}verbs-{run}</c>: the prefix is <c>verbara-</c>, or <c>VERBARA_FUNCTIONAL_DOCKER_PREFIX</c> when it is
    /// set.
    /// </summary>
    public sealed class VerbsAsterisk : IAsyncLifetime
    {
        public const int AmiPort = 5038;
        public const int AriPort = 8088;

        private string? _configDirectory;

        public string Address => (Container ?? throw NotStarted()).NetworkAddress;

        /// <summary>What <c>core show version</c> printed in the container.</summary>
        public string Version { get; private set; } = "";

        // Released by DisposeAsync, which xUnit calls through IAsyncLifetime.
        private INetwork? Network { get; set; }

        private AsteriskContainer? Container { get; set; }

        public Task<DotNet.Testcontainers.Containers.ExecResult> ExecAsync(IList<string> command) =>
            (Container ?? throw NotStarted()).ExecAsync(command);

        public async Task InitializeAsync()
        {
            var prefix = Environment.GetEnvironmentVariable("VERBARA_FUNCTIONAL_DOCKER_PREFIX") ?? "verbara-";
            var name = prefix + "verbs-" + Guid.NewGuid().ToString("N")[..8];
            _configDirectory = WriteConfiguration(Path.Join(Path.GetTempPath(), name));

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
        /// Writes this class's configuration: the shared functional files for the core, HTTP, ARI, AMI and RTP, plus a
        /// PJSIP endpoint looped back to this Asterisk, a dialplan that answers the looped call into Stasis, and a file
        /// log channel.
        /// </summary>
        /// <remarks>
        /// Dialling <c>PJSIP/1005@loop</c> sends an INVITE to 127.0.0.1, which comes back in on the same endpoint and
        /// runs extension 1005, so the call has two PJSIP legs. The endpoint allows <c>ulaw</c> only, keeps media
        /// through Asterisk, and passes hold through to the far leg (<c>moh_passthrough</c>).
        /// </remarks>
        private static string WriteConfiguration(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (var file in new[] { "asterisk.conf", "modules.conf", "http.conf", "ari.conf", "manager.conf", "rtp.conf" })
                File.Copy(Path.Join(DockerPaths.AsteriskConfig, file), Path.Join(directory, file));

            Write(directory, "pjsip.conf", """
                [transport-udp]
                type = transport
                protocol = udp
                bind = 0.0.0.0:5060

                [loop]
                type = aor
                contact = sip:loop@127.0.0.1:5060

                [loop]
                type = identify
                endpoint = loop
                match = 127.0.0.1

                [loop]
                type = endpoint
                context = verbs
                aors = loop
                disallow = all
                allow = ulaw
                direct_media = no
                rtp_symmetric = yes
                moh_passthrough = yes
                moh_suggest = default
                send_pai = no
                """);
            Write(directory, "extensions.conf", $"""
                [general]
                static = yes
                writeprotect = no

                [verbs]
                exten => 1005,1,Answer()
                 same => n,Stasis({Application},bside)
                 same => n,Hangup()
                """);
            Write(directory, "logger.conf", """
                [general]

                [logfiles]
                console => notice,warning,error
                messages.log => notice,warning,error
                """);
            return directory;
        }

        private static void Write(string directory, string file, string text) =>
            File.WriteAllText(Path.Join(directory, file), text + "\n");
    }
}
