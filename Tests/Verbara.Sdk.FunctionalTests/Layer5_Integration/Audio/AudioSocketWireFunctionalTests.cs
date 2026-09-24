namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Audio;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using AriAudioSocketServer = Verbara.Sdk.Ari.Audio.AudioSocketServer;
using VoiceAiAudioSocketOptions = Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketOptions;
using VoiceAiAudioSocketServer = Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketServer;

/// <summary>
/// The round trip neither AudioSocket server had ever made: a real Asterisk, with the stock
/// <c>res_audiosocket.so</c>, dialling into a server built from this repository's own code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Both packages parsed a four-byte header and read <c>0x01</c> as
/// audio for six months, with every unit test green, because every one of those tests built its
/// input with the codec under test. The captured-bytes fixture
/// (<c>Verbara.Sdk.TestInfrastructure.Wire.AudioSocketWireCapture</c>) closes that loop at the unit
/// level; this file closes it end to end. It is the only place in the repository where the bytes
/// are produced by Asterisk at the moment of the assertion rather than replayed from a recording,
/// and the only one that would notice if a future Asterisk changed the format.
/// </para>
/// <para>
/// <b>The dialplan is half the test.</b> <c>docker/functional/asterisk-config/extensions.conf</c>
/// extensions 710 and 711 name the UUIDs asserted below and the ports bound below. That file had no
/// <c>AudioSocket()</c> extension at all before this change, which is the gap that let the wrong
/// format live.
/// </para>
/// <para>
/// <b>Off the PR path</b> (ADR-0051, ADR-0043): this lane runs in the merge queue and on the
/// scheduled matrix, not on every push.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AudioSocketWireFunctionalTests : FunctionalTestBase
{
    /// <summary>The UUID extension 710 names. The same literal appears in the dialplan.</summary>
    private const string AriDialplanUuid = "4f1d9c60-7a2b-4e55-9f3d-2c6a8b0e1d47";

    /// <summary>The UUID extension 711 names. The same literal appears in the dialplan.</summary>
    private const string VoiceAiDialplanUuid = "6b3e2a18-5c94-4d07-8ae1-93f5c7204b6e";

    /// <summary>The port extension 710 dials. Fixed, because a dialplan file cannot learn an ephemeral one.</summary>
    private const int AriPort = 19092;

    /// <summary>The port extension 711 dials.</summary>
    private const int VoiceAiPort = 19093;

    /// <summary>
    /// How long a session may take to arrive. Generous on purpose: this budget only bounds a
    /// failure, and the measured handshake is well inside a second once Asterisk has the call up.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(45);

    public AudioSocketWireFunctionalTests()
        : base("Verbara.Sdk.Ari", "Verbara.Sdk.VoiceAi.AudioSocket")
    {
    }

    /// <summary>
    /// Originates a call into <c>AudioSocket(&lt;uuid&gt;,…:19092)</c> and asserts the
    /// <c>Verbara.Sdk.Ari</c> server starts a stream whose channel id is the UUID the dialplan
    /// named — which requires the three-byte header, <c>Uuid = 0x01</c> and the big-endian UUID
    /// parse all to be right at once.
    /// </summary>
    [Fact]
    public async Task AriAudioSocketServer_ShouldStartASessionCarryingTheDialplanUuid_WhenAsteriskDialsIt()
    {
        var identified = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new AriAudioSocketServer(
            new AudioServerOptions
            {
                AudioSocketPort = AriPort,
                IdleTimeout = HandshakeTimeout
            },
            LoggerFactory.CreateLogger<AriAudioSocketServer>());

        using var subscription = server.OnStreamConnected.Subscribe(stream => identified.TrySetResult(stream));
        await server.StartAsync();

        await OriginateAsync("710", "ari-audiosocket-wire-01");

        var session = await WithinAsync(identified.Task, HandshakeTimeout);

        session.Should().NotBeNull(
            "a real Asterisk dialled this server and sent its identification frame; a null here is "
            + "the handshake never completing, which is exactly the state this repository shipped "
            + "for six months");
        session!.ChannelId.Should().Be(
            AriDialplanUuid,
            "Asterisk identifies the call with the UUID extension 710 names, sixteen bytes in RFC "
            + "4122 order behind a three-byte header — a wrong header, a wrong type byte or a "
            + "little-endian Guid read each produce a different string here");
    }

    /// <summary>
    /// The same round trip for <c>Verbara.Sdk.VoiceAi.AudioSocket</c>, whose codec is a separate
    /// implementation of the same protocol. Two parsers, one wire: a test that exercised only one
    /// of them would leave the other free to drift again.
    /// </summary>
    [Fact]
    public async Task VoiceAiAudioSocketServer_ShouldStartASessionCarryingTheDialplanUuid_WhenAsteriskDialsIt()
    {
        var identified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new VoiceAiAudioSocketServer(
            new VoiceAiAudioSocketOptions
            {
                Port = VoiceAiPort,
                ConnectionTimeout = HandshakeTimeout
            },
            LoggerFactory.CreateLogger<VoiceAiAudioSocketServer>());

        server.OnSessionStarted += session =>
        {
            identified.TrySetResult(session.ChannelId.ToString());
            return ValueTask.CompletedTask;
        };

        await server.StartAsync(CancellationToken.None);

        await OriginateAsync("711", "voiceai-audiosocket-wire-01");

        var channelId = await WithinAsync(identified.Task, HandshakeTimeout);

        channelId.Should().NotBeNull(
            "a real Asterisk dialled this server and sent its identification frame; a null here is "
            + "the handshake never completing");
        channelId.Should().Be(
            VoiceAiDialplanUuid,
            "this codec is a second, independent implementation of the same protocol, and it must "
            + "read the same UUID off the same wire as the ARI one");
    }

    /// <summary>
    /// Originates <c>Local/{exten}@test-functional</c>. The <c>;2</c> half runs the extension and
    /// hands the call to <c>AudioSocket()</c>; the <c>;1</c> half is parked in <c>Wait</c> so the
    /// call stays up while the handshake is measured.
    /// </summary>
    private async Task OriginateAsync(string exten, string actionId)
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = $"Local/{exten}@test-functional",
            Application = "Wait",
            Data = "20",
            IsAsync = true,
            Timeout = 20000,
            ActionId = actionId
        });
    }

    /// <summary>
    /// Awaits <paramref name="task"/> and returns <c>null</c> if it does not complete within
    /// <paramref name="timeout"/>, so the failure is an assertion with a reason rather than a bare
    /// <see cref="TimeoutException"/>. No wall-clock barrier: the timeout is the task's own
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
