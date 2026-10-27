namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio;

using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
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
}
