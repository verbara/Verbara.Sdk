namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio;

using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// The end-to-end claim of the <c>externalmedia-returns-a-channel-id-that-finds-its-stream</c>
/// change, measured rather than asserted: a real Asterisk creates a real <c>externalMedia</c>
/// channel, dials this process over AudioSocket, and the stream is found by the id the create call
/// handed back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a functional test and not a unit test.</b> With a mocked <c>IAriChannelsResource</c> the
/// test chooses both the <c>Channel.Id</c> that comes back and the UUID its own fake client puts on
/// the wire, so <c>GetStream(Channel.Id)</c> can be made to hit with the defect fully present. That
/// closed loop is the whole subject of this change, so the claim can only be settled by a party that
/// is not the test: Asterisk mints the channel, Asterisk sends the identification frame, and the two
/// values are compared afterwards. The argument-capture unit test in
/// <c>Verbara.Sdk.Activities.Tests</c> pins what the activity <em>sends</em>; this pins what
/// Asterisk <em>does</em> with it.
/// </para>
/// <para>
/// <b>The Stasis subscription is load-bearing, not boilerplate.</b> Asterisk's <c>externalMedia</c>
/// validates only that <c>app</c> is non-empty — it never checks the application is registered — so
/// the create returns HTTP 200 against no subscriber at all. The channel then enters Stasis with
/// nobody listening, Asterisk hangs it up, and the server's table entry is gone well inside the
/// activity's 200 ms poll. A test written from the probe alone therefore times out and reads as the
/// fix not working. <see cref="AriClientFactory"/> opens the ARI event WebSocket with
/// <c>app=test-app</c>, which is what registers it, so the client is connected <em>before</em> the
/// activity is started.
/// </para>
/// <para>
/// <b>The activity is constructed with <c>Encapsulation</c> and nothing else.</b> No
/// <c>Transport</c>, no <c>Format</c>: those are exactly the values the class under test is supposed
/// to derive. Supplying <c>Transport = "tcp"</c> here would make the test pass over a broken
/// <c>ExternalMediaActivity</c>, which is the failure mode this file exists to rule out.
/// </para>
/// <para>
/// <b>Off the PR path</b> (ADR-0051, ADR-0043): this lane runs in the merge queue, on the scheduled
/// matrix, and on a pull request only when it carries the <c>ci:functional</c> label.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ExternalMediaChannelIdFunctionalTests : FunctionalTestBase
{
    /// <summary>
    /// The Stasis application this test subscribes and creates against. It is the name
    /// <see cref="AriClientFactory"/> opens its event WebSocket with, and the two must agree: a
    /// create naming an application nobody is subscribed to still returns HTTP 200 and then loses
    /// the channel.
    /// </summary>
    private const string StasisApp = "test-app";

    /// <summary>
    /// The port the AudioSocket server binds on this host. Fixed rather than ephemeral because the
    /// value travels to Asterisk inside <c>external_host</c>, and deliberately neither 19092 nor
    /// 19093: those two are named by dialplan extensions 710 and 711
    /// (<c>docker/functional/asterisk-config/extensions.conf</c>) and are bound by
    /// <see cref="AudioSocketWireFunctionalTests"/>.
    /// </summary>
    private const int AudioSocketPort = 19094;

    /// <summary>
    /// The name Asterisk resolves to the Docker host gateway.
    /// <c>AsteriskContainer</c> maps it with <c>WithExtraHost("host.docker.internal",
    /// "host-gateway")</c>; the server under test runs in this process, on the host, not in the
    /// container.
    /// </summary>
    private const string HostGateway = "host.docker.internal";

    /// <summary>
    /// The encapsulation asked for, spelled as Asterisk's own error message spells it. Asterisk
    /// compares it with <c>strcasecmp</c>, so the casing is not what is being tested here.
    /// </summary>
    private const string AudioSocketEncapsulation = "audiosocket";

    /// <summary>
    /// How long Asterisk has to connect and identify itself, used both as the activity's
    /// <c>ConnectionTimeout</c> and as the server's idle deadline. Generous on purpose: this budget
    /// only bounds a failure. <c>chan_audiosocket</c> makes its TCP connection <em>during</em> the
    /// create call, so on the success path nothing waits at all.
    /// </summary>
    private static readonly TimeSpan ConnectionBudget = TimeSpan.FromSeconds(45);

    public ExternalMediaChannelIdFunctionalTests()
        : base("Verbara.Sdk.Ari")
    {
    }

    /// <summary>
    /// Creates an AudioSocket <c>externalMedia</c> channel through <see cref="ExternalMediaActivity"/>
    /// against a real Asterisk and asserts that the stream the activity resolved is registered under
    /// the id the create call returned, and that the UUID Asterisk put in its identification frame is
    /// that same id.
    /// </summary>
    [Fact]
    public async Task ExternalMediaActivity_ShouldResolveItsStreamByTheReturnedChannelId_WhenAsteriskConnectsOverAudioSocket()
    {
        // The wire's own account of the identifier, taken from the server rather than from the
        // activity: OnStreamConnected fires when a session's ChannelId has been parsed out of
        // Asterisk's identification frame, so this value is produced by Asterisk and is not
        // anything this test chose.
        var identified = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new AudioSocketServer(
            new AudioServerOptions
            {
                AudioSocketPort = AudioSocketPort,
                ListenAddress = "0.0.0.0",
                IdleTimeout = ConnectionBudget
            },
            LoggerFactory.CreateLogger<AudioSocketServer>());

        using var subscription = server.OnStreamConnected.Subscribe(stream => identified.TrySetResult(stream));

        // Bound before the create, because chan_audiosocket connects during the create call: a
        // create against a port nothing is listening on is HTTP 500, not a channel that connects
        // later (probe-capture.txt, PART 3).
        await server.StartAsync();

        // Subscribes the Stasis application. See the class remarks — without this the create still
        // succeeds and the channel is then hung up under the poll.
        await using var ariClient = AriClientFactory.Create(LoggerFactory, application: StasisApp);
        await ariClient.ConnectAsync();

        await using var activity = new ExternalMediaActivity(ariClient, server)
        {
            App = StasisApp,
            ExternalHost = $"{HostGateway}:{AudioSocketPort}",

            // The only thing configured beyond the two required properties. Transport, the
            // identification UUID and the channel id are all the activity's own to derive, and a
            // test that supplied them would measure its own arrange instead of the class.
            Encapsulation = AudioSocketEncapsulation,
            ConnectionTimeout = ConnectionBudget
        };

        // Recorded rather than thrown so the assertions below all run and each one carries its own
        // reason. A bare await would end the test on Asterisk's exception with no statement of what
        // was expected of it.
        var failure = await Record.ExceptionAsync(async () => await activity.StartAsync());

        // Read off the server, not off the activity, and reported in the first assertion's reason.
        // A timeout here has two very different causes — Asterisk never connected, or it connected
        // and registered under a key that is not the channel id — and only the registered keys tell
        // them apart. Without this the two are one indistinguishable red.
        var registered = server.ActiveStreams.Select(stream => stream.ChannelId).ToArray();

        try
        {
            failure.Should().BeNull(
                "an AudioSocket external media create is supposed to succeed and resolve its stream "
                + "end to end; instead StartAsync ended with {0}, with the returned channel id {1} "
                + "and these streams registered on the server: [{2}] — a timeout with a stream "
                + "registered under some other key is the defect itself, the two identifiers having "
                + "come apart",
                failure?.ToString() ?? "no exception",
                activity.Channel?.Id ?? "no channel",
                registered.Length == 0 ? "none" : string.Join(", ", registered));

            activity.Channel.Should().NotBeNull(
                "a successful create returns the channel Asterisk made, and every assertion below "
                + "compares against its id");

            activity.AudioStream.Should().NotBeNull(
                "the activity resolves its stream by calling GetStream(Channel.Id) on the server it "
                + "was handed, so a null here is that lookup never hitting — the exact defect this "
                + "change fixes, where the table was keyed by the wire UUID and searched by an "
                + "Asterisk-minted channel id");

            activity.AudioStream!.ChannelId.Should().Be(
                activity.Channel!.Id,
                "the stream's ChannelId is the key its server registered it under, and the claim of "
                + "this change is that an ARI channel id is that key: one identifier is sent as both "
                + "channelId and data, so Channel.Id and the registration key are the same string");

            var wireStream = await WithinAsync(identified.Task, ConnectionBudget);

            wireStream.Should().NotBeNull(
                "a real Asterisk connected to this server during the create call and must have sent "
                + "its AudioSocket identification frame; a null here is that frame never arriving");

            wireStream!.ChannelId.Should().Be(
                activity.Channel!.Id,
                "this value was parsed out of the sixteen bytes Asterisk sent, not out of anything "
                + "this test chose, and it is what settles the claim: the identification UUID and "
                + "the id the create call handed back are one value");

            activity.Channel!.Id.Should().Be(
                activity.Channel.Id.ToLowerInvariant(),
                "Asterisk echoes channelId back verbatim and does not normalise it (probe-capture.txt, "
                + "RUN U), while AudioSocketSession.ParseUuid renders the wire bytes with "
                + "Guid.ToString() into an ordinal dictionary — so only a canonical lowercase "
                + "identifier can ever be found again");

            Guid.TryParseExact(activity.Channel!.Id, "D", out _).Should().BeTrue(
                "the identifier the activity minted has to be a canonical hyphenated UUID for "
                + "Asterisk to accept it as data at all: a value that does not parse is HTTP 500 at "
                + "the create with the listener up (probe-capture.txt, correction #4)");
        }
        finally
        {
            if (activity.Channel is not null)
                await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(activity.Channel.Id));
        }
    }

    /// <summary>
    /// Awaits <paramref name="task"/> and returns <see langword="null"/> if it does not complete
    /// within <paramref name="timeout"/>, so the failure is an assertion with a reason rather than a
    /// bare <see cref="TimeoutException"/>. No wall-clock barrier: the timeout is the task's own
    /// deadline, not a sleep the test races (ADR-0004 / ADR-0045).
    /// </summary>
    private static async Task<T?> WithinAsync<T>(Task<T> task, TimeSpan timeout) where T : class
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
