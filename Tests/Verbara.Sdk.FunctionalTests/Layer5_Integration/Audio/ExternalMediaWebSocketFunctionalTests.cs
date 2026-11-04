namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio;

using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="ExternalMediaActivity"/> over WebSocket against a real Asterisk: a real <c>externalMedia</c> channel
/// created with <c>transport=websocket</c>, whose chan_websocket connects back to this process through the
/// <c>websocket_client</c> entry <c>sdk-media</c> (<c>docker/functional/asterisk-config/websocket_client.conf</c>), and
/// whose stream the activity finds by the channel id the create returned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it needs Asterisk.</b> The key is the <c>channel_id</c> of the <c>MEDIA_START</c> chan_websocket sends as
/// the connection's first frame, and the channel id is the one Asterisk minted at the create: this activity passes no
/// <c>channelId</c> for WebSocket. Only a real Asterisk produces both values, so only it can show they are one.
/// Every call through the entry upgrades on the same fixed URI, which is why the path cannot be the key.
/// </para>
/// <para>
/// <b>The Stasis subscription is load-bearing.</b> As for the AudioSocket test beside this one: <c>externalMedia</c>
/// returns HTTP 200 for an application nobody subscribes to and then loses the channel, so the ARI client is
/// connected with <c>app=test-app</c> before the activity starts.
/// </para>
/// <para>
/// <b>Off the PR path</b>: this lane runs in the merge queue, on the scheduled matrix, and on a pull request only
/// when it carries the <c>ci:functional</c> label.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ExternalMediaWebSocketFunctionalTests : FunctionalTestBase
{
    /// <summary>The Stasis application <see cref="AriClientFactory"/> subscribes and the create names.</summary>
    private const string StasisApp = "test-app";

    /// <summary>
    /// The <c>websocket_client.conf</c> section the create names as <c>external_host</c>. Its URI points at
    /// <see cref="WebSocketPort"/> on the Docker host gateway, so the two constants change with that file.
    /// </summary>
    private const string Connection = "sdk-media";

    /// <summary>
    /// The port the WebSocket audio server binds on this host, the one <c>websocket_client.conf</c>'s <c>sdk-media</c>
    /// URI names. Fixed because the configuration file cannot learn an ephemeral one, and neither 19092, 19093 (the
    /// dialplan's AudioSocket extensions) nor 19094 (the externalMedia AudioSocket test).
    /// </summary>
    private const int WebSocketPort = 19095;

    /// <summary>
    /// How long Asterisk has to connect and name its call: the activity's <c>ConnectionTimeout</c>. It only bounds a
    /// failure; chan_websocket connects during the create call, and its <c>MEDIA_START</c> follows the upgrade.
    /// </summary>
    private static readonly TimeSpan ConnectionBudget = TimeSpan.FromSeconds(20);

    public ExternalMediaWebSocketFunctionalTests()
        : base("Verbara.Sdk.Ari")
    {
    }

    /// <summary>
    /// Creates a WebSocket <c>externalMedia</c> channel through <see cref="ExternalMediaActivity"/> against a real
    /// Asterisk and asserts that the stream the activity resolved, and the stream the server announced, are keyed by
    /// the channel id the create returned.
    /// </summary>
    [Fact]
    public async Task ExternalMediaActivity_ShouldResolveItsStreamByTheReturnedChannelId_WhenAsteriskConnectsOverWebSocket()
    {
        // The wire's own account of the key, read off the server: a stream is announced once its first frame has
        // named it, so this value is the channel_id Asterisk sent, not anything this test chose.
        var announced = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The default MediaStartTimeout: this is the re-key, measured end to end.
        await using var server = new WebSocketAudioServer(
            new AudioServerOptions
            {
                WebSocketPort = WebSocketPort,
                ListenAddress = "0.0.0.0",
                IdleTimeout = ConnectionBudget,
            },
            LoggerFactory.CreateLogger<WebSocketAudioServer>());

        using var subscription = server.OnStreamConnected.Subscribe(stream => announced.TrySetResult(stream));
        await server.StartAsync();

        await using var ariClient = AriClientFactory.Create(LoggerFactory, application: StasisApp);
        await ariClient.ConnectAsync();

        // The image must carry the two modules the route needs; without them the create fails for a reason that is
        // not this test's subject, so it is said here first.
        var webSocketClient = await ariClient.Asterisk.GetModuleAsync("res_websocket_client.so");
        var channelDriver = await ariClient.Asterisk.GetModuleAsync("chan_websocket.so");
        webSocketClient.Status.Should().Be("Running", "the websocket_client entry is served by res_websocket_client");
        channelDriver.Status.Should().Be("Running", "the externalMedia WebSocket channel is chan_websocket's");

        await using var activity = new ExternalMediaActivity(ariClient, webSocketServer: server)
        {
            App = StasisApp,
            ExternalHost = Connection,
            Encapsulation = "none",
            Transport = "websocket",
            ConnectionTimeout = ConnectionBudget,
        };

        var failure = await Record.ExceptionAsync(async () => await activity.StartAsync());

        // Read off the server, not off the activity: a timeout has two causes, Asterisk never connecting, or a stream
        // announced under a key that is not the channel id, and only the announced keys tell them apart.
        var keys = server.ActiveStreams.Select(stream => stream.ChannelId).ToArray();

        try
        {
            failure.Should().BeNull(
                "a WebSocket external media create resolves its stream by the channel id the create returned; instead "
                + "StartAsync ended with {0}, with the returned channel id {1} and these streams on the server: [{2}]",
                failure?.ToString() ?? "no exception",
                activity.Channel?.Id ?? "no channel",
                keys.Length == 0 ? "none" : string.Join(", ", keys));

            Assert.True(activity.Channel is not null,
                "activity.Channel is null, but a successful create returns the channel Asterisk made");

            activity.AudioStream.Should().NotBeNull(
                "the activity calls GetStream(Channel.Id) on the WebSocket server it was handed");
            activity.AudioStream!.ChannelId.Should().Be(
                activity.Channel!.Id, "the stream is keyed by the call its MEDIA_START names, which is the channel id");
            activity.AudioStream.Should().BeAssignableTo<IChanWebSocketSession>("a found stream says its transport by type");

            var wireStream = await announced.Task.WaitAsync(ConnectionBudget);
            wireStream.ChannelId.Should().Be(
                activity.Channel!.Id,
                "this value is the channel_id of the MEDIA_START Asterisk sent, and the create returned the same id");
            server.GetStream(Connection).Should().BeNull("the fixed path every call upgrades on is not a key");
        }
        finally
        {
            if (activity.Channel is not null)
                await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(activity.Channel.Id));
        }
    }

    /// <summary>
    /// <c>transport_data</c> <c>f(json)</c> chooses JSON for one channel only: two creates through the same
    /// <c>websocket_client</c> entry, one with it and one without, and each connection's <c>MEDIA_START</c> arrives in
    /// the format its own create asked for.
    /// </summary>
    /// <remarks>
    /// The create without the option is the control: the entry has no format of its own, so a JSON
    /// <c>MEDIA_START</c> on the first connection can only come from the request.
    /// </remarks>
    [Fact]
    public async Task CreateExternalMediaAsync_ShouldOpenAJsonConnection_WhenOnlyThatCreateCarriesTransportDataJson()
    {
        await using var server = await StartServerAsync();
        await using var ariClient = await ConnectAriAsync();

        var json = await CreateSessionAsync(ariClient, server, transportData: "f(json)");
        try
        {
            var plain = await CreateSessionAsync(ariClient, server, transportData: null);
            try
            {
                json.Session.MediaStart!.ControlMessageFormat.Should().Be(
                    ChanWebSocketControlMessageFormat.Json,
                    "the create asked for f(json), and the MEDIA_START of its connection is read off the wire");
                json.Session.MediaStart.ChannelId.Should().Be(json.Channel.Id);

                plain.Session.MediaStart!.ControlMessageFormat.Should().Be(
                    ChanWebSocketControlMessageFormat.PlainText,
                    "the same entry with no transport_data keeps Asterisk's default, plain text");
                plain.Session.MediaStart.ChannelId.Should().Be(plain.Channel.Id);
            }
            finally
            {
                await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(plain.Channel.Id));
            }
        }
        finally
        {
            await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(json.Channel.Id));
        }
    }

    /// <summary>
    /// A variable sent with the create is set on the channel Asterisk made, and on an <c>f(json)</c> connection it is
    /// also in the <c>channel_variables</c> of the <c>MEDIA_START</c>.
    /// </summary>
    /// <remarks>
    /// The value is unique per run, so a channel left behind by another test cannot answer for this one. The
    /// <c>MEDIA_START</c> check is the wire's own account of the variable: the channel had it before it connected.
    /// </remarks>
    [Fact]
    public async Task CreateExternalMediaAsync_ShouldSetTheVariableOnTheChannel_WhenTheRequestCarriesVariables()
    {
        const string variable = "VSDK_FT_VAR";
        var value = $"ft-{Guid.NewGuid():N}";

        await using var server = await StartServerAsync();
        await using var ariClient = await ConnectAriAsync();

        var created = await CreateSessionAsync(
            ariClient, server, transportData: "f(json)", new Dictionary<string, string> { [variable] = value });
        try
        {
            var read = await ariClient.Channels.GetVariableAsync(created.Channel.Id, variable);
            read.Value.Should().Be(value, "the create sent the variable in its JSON body, and Asterisk set it on the channel");

            created.Session.MediaStart!.ChannelVariables.Should().NotBeNull(
                "an f(json) MEDIA_START carries the channel's variables");
            created.Session.MediaStart.ChannelVariables!.Should().Contain(variable, value);
        }
        finally
        {
            await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(created.Channel.Id));
        }
    }

    /// <summary>
    /// Barge-in: with audio this process wrote still queued in Asterisk, <c>SendFlushMediaAsync</c> empties the queue,
    /// as the <c>STATUS</c> after it reports, in each control format.
    /// </summary>
    /// <remarks>
    /// The <c>STATUS</c> before the flush is the precondition that lets the test fail: it must show a queue above zero,
    /// or a zero afterwards would prove nothing.
    /// </remarks>
    /// <param name="transportData">The create's <c>transport_data</c>: <see langword="null"/> for plain text, <c>f(json)</c> for JSON.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("f(json)")]
    public async Task SendFlushMediaAsync_ShouldEmptyTheQueueAsteriskHolds_WhenAudioIsStillQueued(string? transportData)
    {
        await using var server = await StartServerAsync();
        await using var ariClient = await ConnectAriAsync();

        var created = await CreateSessionAsync(ariClient, server, transportData);
        try
        {
            var session = created.Session;
            using var replies = new ControlReplies(session);

            // 300 frames of 20 ms is six seconds of audio, written at once: Asterisk plays it in real time, so most of
            // it is still queued when the status is read.
            await WriteSilenceAsync(session, frames: 300);
            // fence-allow: SETTLE — lets Asterisk queue the frames just written before the status reads the queue (the lab's measured sequence)
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            var before = await replies.RequestStatusAsync();
            before.QueueLength.Should().BeGreaterThan(0, "the audio written above is still queued, or the flush is untested");

            await session.SendFlushMediaAsync();
            // fence-allow: SETTLE — lets Asterisk act on FLUSH_MEDIA, which it never acknowledges, before the status reads the queue
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            var after = await replies.RequestStatusAsync();
            after.QueueLength.Should().Be(0, "FLUSH_MEDIA drops the audio Asterisk had queued");
            after.ChannelId.Should().Be(created.Channel.Id);
            replies.Errors.Should().BeEmpty("Asterisk accepted every command");
        }
        finally
        {
            await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(created.Channel.Id));
        }
    }

    /// <summary>
    /// <c>SendPauseMediaAsync</c> freezes the queue and <c>SendContinueMediaAsync</c> releases it, as three
    /// <c>STATUS</c> replies report, in each control format.
    /// </summary>
    /// <remarks>
    /// The two statuses during the pause are a second apart. Played audio would leave the queue in that second, so an
    /// equal length shows nothing played; the shorter queue after the continue shows playback resumed.
    /// </remarks>
    /// <param name="transportData">The create's <c>transport_data</c>: <see langword="null"/> for plain text, <c>f(json)</c> for JSON.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("f(json)")]
    public async Task SendPauseMediaAsync_ShouldFreezeTheQueueUntilContinue_WhenAudioIsStillQueued(string? transportData)
    {
        await using var server = await StartServerAsync();
        await using var ariClient = await ConnectAriAsync();

        var created = await CreateSessionAsync(ariClient, server, transportData);
        try
        {
            var session = created.Session;
            using var replies = new ControlReplies(session);

            // 200 frames: four seconds of audio, enough to outlast the pause and the second after the continue.
            await WriteSilenceAsync(session, frames: 200);
            // fence-allow: SETTLE — lets Asterisk queue the frames just written before the pause (the lab's measured sequence)
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            await session.SendPauseMediaAsync();
            // fence-allow: SETTLE — lets Asterisk act on PAUSE_MEDIA, which it never acknowledges, before the status reads the queue
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            var paused = await replies.RequestStatusAsync();
            // fence-allow: SETTLE — the paused window whose unchanged queue IS the measured claim; without it equal lengths would prove nothing
            await Task.Delay(TimeSpan.FromSeconds(1));
            var stillPaused = await replies.RequestStatusAsync();

            await session.SendContinueMediaAsync();
            // fence-allow: SETTLE — the second of resumed playback that must shorten the queue; CONTINUE_MEDIA is never acknowledged
            await Task.Delay(TimeSpan.FromSeconds(1));
            var resumed = await replies.RequestStatusAsync();

            paused.MediaPaused.Should().BeTrue("PAUSE_MEDIA was accepted");
            paused.QueueLength.Should().BeGreaterThan(0, "the audio written above is still queued, or the pause is untested");
            stillPaused.MediaPaused.Should().BeTrue("nothing continued the media yet");
            stillPaused.QueueLength.Should().Be(
                paused.QueueLength, "a paused queue plays nothing, so a second later it holds the same frames");

            resumed.MediaPaused.Should().BeFalse("CONTINUE_MEDIA was accepted");
            resumed.QueueLength.Should().BeLessThan(
                stillPaused.QueueLength, "playback resumed, and a second of it left the queue");
            replies.Errors.Should().BeEmpty("Asterisk accepted every command");
        }
        finally
        {
            await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(created.Channel.Id));
        }
    }

    /// <summary>The audio format the scenarios create with: 16 kHz signed linear, so a 20 ms frame is 640 bytes.</summary>
    private const string MediaFormat = "slin16";

    /// <summary>One 20 ms frame of <see cref="MediaFormat"/> silence.</summary>
    private static readonly byte[] SilenceFrame = new byte[640];

    /// <summary>How long a <c>STATUS</c> may take to answer a <c>GET_STATUS</c>; it bounds a failure only.</summary>
    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(5);

    private async Task<WebSocketAudioServer> StartServerAsync()
    {
        var server = new WebSocketAudioServer(
            new AudioServerOptions
            {
                WebSocketPort = WebSocketPort,
                ListenAddress = "0.0.0.0",
                IdleTimeout = ConnectionBudget,
            },
            LoggerFactory.CreateLogger<WebSocketAudioServer>());
        await server.StartAsync();
        return server;
    }

    private async Task<Verbara.Sdk.Ari.Client.AriClient> ConnectAriAsync()
    {
        var ariClient = AriClientFactory.Create(LoggerFactory, application: StasisApp);
        await ariClient.ConnectAsync();
        return ariClient;
    }

    /// <summary>
    /// Creates a WebSocket <c>externalMedia</c> channel through the request overload, letting Asterisk mint the id, and
    /// waits for the stream whose <c>MEDIA_START</c> names that id.
    /// </summary>
    private static async Task<(AriChannel Channel, IChanWebSocketSession Session)> CreateSessionAsync(
        Verbara.Sdk.Ari.Client.AriClient ariClient,
        WebSocketAudioServer server,
        string? transportData,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var channel = await ariClient.Channels.CreateExternalMediaAsync(new AriExternalMediaRequest
        {
            App = StasisApp,
            ExternalHost = Connection,
            Format = MediaFormat,
            Encapsulation = "none",
            Transport = "websocket",
            TransportData = transportData,
            Variables = variables,
        });

        var deadline = DateTime.UtcNow + ConnectionBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (server.GetStream(channel.Id) is IChanWebSocketSession { MediaStart: not null } session)
                return (channel, session);
            // fence-allow: LOOP-DRIVER — paces the wait for the stream's MEDIA_START; ConnectionBudget bounds it
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(channel.Id));
        var keys = server.ActiveStreams.Select(stream => stream.ChannelId).ToArray();
        throw new TimeoutException(
            $"No stream named channel {channel.Id} within {ConnectionBudget}; streams on the server: "
            + (keys.Length == 0 ? "none" : string.Join(", ", keys)));
    }

    private static async Task WriteSilenceAsync(IAudioStream stream, int frames)
    {
        for (var i = 0; i < frames; i++)
            await stream.WriteFrameAsync(SilenceFrame);
    }

    /// <summary>
    /// The replies a session's <see cref="IChanWebSocketSession.ControlMessages"/> delivers: each <c>STATUS</c> in
    /// arrival order, and every <c>ERROR</c> for the failure message.
    /// </summary>
    private sealed class ControlReplies : IDisposable
    {
        private readonly IChanWebSocketSession _session;
        private readonly Channel<ChanWebSocketStatus> _statuses = Channel.CreateUnbounded<ChanWebSocketStatus>();
        private readonly IDisposable _subscription;
        private readonly List<string> _errors = [];

        public ControlReplies(IChanWebSocketSession session)
        {
            _session = session;
            _subscription = session.ControlMessages.Subscribe(message =>
            {
                switch (message)
                {
                    case ChanWebSocketStatus status:
                        _statuses.Writer.TryWrite(status);
                        break;
                    case ChanWebSocketError error:
                        lock (_errors) _errors.Add(error.ErrorText);
                        break;
                }
            });
        }

        public IReadOnlyList<string> Errors
        {
            get { lock (_errors) return [.. _errors]; }
        }

        /// <summary>Writes <c>GET_STATUS</c> and returns the next <c>STATUS</c>, after any earlier one is dropped.</summary>
        public async Task<ChanWebSocketStatus> RequestStatusAsync()
        {
            while (_statuses.Reader.TryRead(out _))
            {
                // Drop a STATUS no request is waiting for, so the next read answers this GET_STATUS.
            }

            await _session.SendGetStatusAsync();
            using var timeout = new CancellationTokenSource(ReplyBudget);
            try
            {
                return await _statuses.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"No STATUS answered GET_STATUS within {ReplyBudget}; errors: [{string.Join(" | ", Errors)}]", ex);
            }
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _statuses.Writer.TryComplete();
        }
    }
}
