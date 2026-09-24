using Verbara.Sdk;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Activities.Activities;

/// <summary>
/// Activity that creates an ExternalMedia channel, waits for Asterisk
/// to connect back via AudioSocket or WebSocket, and provides an IAudioStream
/// for bidirectional audio streaming.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the AudioSocket branch is routed.</b> This activity looks the stream up by
/// <see cref="AriChannel.Id"/> on whichever server it was handed, and that only works for
/// AudioSocket because this class arranges it: it mints one identifier and sends it as both
/// <c>channelId</c> and <c>data</c>, so the ARI channel id and the key
/// <see cref="AudioSocketServer"/> registers the stream under are the same string.
/// </para>
/// <para>
/// Nothing arranges that for <see cref="WebSocketAudioServer"/>. It keys its table on the last path
/// segment of the HTTP upgrade request URL with the query string stripped, while a non-AudioSocket
/// create leaves <see cref="AriChannel.Id"/> an Asterisk-minted uniqueid such as
/// <c>1790244226.1</c>. This class does not make those two match, so a
/// <see cref="WebSocketAudioServer"/> passed to this activity is expected to poll out
/// <see cref="ConnectionTimeout"/> and throw <see cref="TimeoutException"/> however well the
/// WebSocket connection itself went — unless that request path happens to carry the very id
/// Asterisk minted, which is exactly what no probe has measured. That is why this branch is
/// documented here rather than fixed.
/// </para>
/// </remarks>
public sealed class ExternalMediaActivity : AriActivityBase
{
    /// <summary>
    /// The value Asterisk's <c>externalMedia</c> expects for AudioSocket encapsulation. Asterisk
    /// compares it with <c>strcasecmp</c>, which is why every comparison against it here is
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>: a caller who writes "AudioSocket" reaches
    /// chan_audiosocket exactly as one who writes "audiosocket" does, and an ordinal comparison
    /// would quietly route the first down the RTP path instead.
    /// </summary>
    private const string AudioSocketEncapsulation = "audiosocket";

    /// <summary>
    /// The only transport Asterisk accepts under AudioSocket encapsulation. Anything else is
    /// <c>HTTP 400 — "transport must be 'tcp' for audiosocket encapsulation"</c>, measured on
    /// Asterisk 22.9.0 and 23.4.1 (probe-capture.txt, RUN G).
    /// </summary>
    private const string AudioSocketTransport = "tcp";

    private readonly AudioSocketServer? _audioSocketServer;
    private readonly WebSocketAudioServer? _webSocketServer;
    private IAudioStream? _audioStream;

    /// <summary>Stasis application name.</summary>
    public required string App { get; init; }

    /// <summary>External host address for the audio connection (e.g., "192.168.1.100:9092").</summary>
    public required string ExternalHost { get; init; }

    /// <summary>Audio format. Default: "slin16".</summary>
    public string Format { get; init; } = "slin16";

    /// <summary>
    /// Encapsulation type (e.g., "audiosocket"), or null to let the activity derive it. Left null
    /// with an <see cref="AudioSocketServer"/> supplied it becomes "audiosocket"; left null with no
    /// AudioSocket server it stays null, which Asterisk reads as rtp/udp. Setting it to a
    /// non-AudioSocket value while supplying an <see cref="AudioSocketServer"/> is a contradiction
    /// and <see cref="AriActivityBase.StartAsync(CancellationToken)"/> throws
    /// <see cref="InvalidOperationException"/> rather than
    /// creating a channel that can never carry that server's stream.
    /// </summary>
    public string? Encapsulation { get; init; }

    /// <summary>
    /// Transport type (e.g., "websocket"), or null to let the activity derive it. Under AudioSocket
    /// encapsulation a null transport becomes "tcp", the only one Asterisk accepts there. An
    /// explicit value is always sent as given, so a wrong one fails at the create call with
    /// Asterisk's own message instead of being silently rewritten.
    /// </summary>
    public string? Transport { get; init; }

    /// <summary>Timeout waiting for Asterisk to connect to the audio server.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The audio stream once the connection is established.</summary>
    public IAudioStream? AudioStream => _audioStream;

    /// <summary>The ARI channel created for this ExternalMedia session.</summary>
    public AriChannel? Channel { get; private set; }

    public ExternalMediaActivity(IAriClient ariClient, AudioSocketServer? audioSocketServer = null, WebSocketAudioServer? webSocketServer = null)
        : base(ariClient)
    {
        _audioSocketServer = audioSocketServer;
        _webSocketServer = webSocketServer;
    }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        // 1. Derive the request from the audio server this activity was handed.
        //
        // An AudioSocketServer with Encapsulation left null is the shape callers actually get, and
        // Asterisk reads an absent encapsulation as rtp/udp: it answers HTTP 200 with a UnicastRTP
        // channel, nothing ever connects to the AudioSocket server, and the poll below burns the
        // whole of ConnectionTimeout before throwing (probe-capture.txt, RUN H and RUN H+).
        var encapsulation = Encapsulation ?? (_audioSocketServer is not null ? AudioSocketEncapsulation : null);

        // strcasecmp on Asterisk's side — see AudioSocketEncapsulation.
        var isAudioSocket = string.Equals(encapsulation, AudioSocketEncapsulation, StringComparison.OrdinalIgnoreCase);

        if (_audioSocketServer is not null && !isAudioSocket)
        {
            // The contradiction case, refused rather than attempted. Asterisk would happily create
            // the channel on the encapsulation asked for; the AudioSocket server would simply never
            // be connected to, and this activity would then wait out ConnectionTimeout and report a
            // TimeoutException — "the audio server did not connect" — for what is a configuration
            // mistake made before any connection was possible. Failing here keeps the diagnosis
            // where the cause is, and keeps a doomed channel from being created at all.
            throw new InvalidOperationException(
                $"An AudioSocketServer was supplied but Encapsulation is \"{encapsulation}\", not \"{AudioSocketEncapsulation}\". " +
                $"Asterisk would create a {encapsulation} channel that never connects to that server, and this activity " +
                $"would wait out ConnectionTimeout ({ConnectionTimeout}) and report a connection failure instead of this " +
                "contradiction. Set Encapsulation to \"audiosocket\" — or leave it null, which derives it from the " +
                "server — or do not pass an AudioSocketServer.");
        }

        var transport = Transport;
        string? data = null;
        string? channelId = null;

        if (isAudioSocket)
        {
            // Asterisk rejects audiosocket on any other transport with HTTP 400 "transport must be
            // 'tcp' for audiosocket encapsulation" (RUN G) — and the activity never sent one, which
            // is why every AudioSocket caller failed at the create call. An explicit Transport still
            // wins: a wrong one then fails with Asterisk's own message rather than being rewritten.
            transport ??= AudioSocketTransport;

            // ONE identifier into BOTH parameters, which is the whole of this change.
            //
            // They are different things. `data` is the UUID Asterisk puts in the AudioSocket
            // identification frame, and it is what AudioSocketServer keys its stream table by;
            // `channelId` is the id Asterisk assigns the ARI channel, and comes back as Channel.Id.
            // Passing only `data` leaves Channel.Id an Asterisk-minted uniqueid like "1790244226.1"
            // that appears in no table (RUN C), so a consumer holding only a channel id — a
            // StasisStart or ChannelHangupRequest handler — can never find the stream. Passing the
            // same value as both makes Channel.Id the key (RUN A). Asterisk also requires `data` to
            // be present at all: without it, HTTP 400 "data can not be empty" (RUN D).
            //
            // Guid.ToString() — canonical lowercase, hyphenated — is load-bearing, for two measured
            // reasons, not for tidiness:
            //   1. AudioSocketSession.ParseUuid renders the sixteen wire bytes with
            //      new Guid(bytes, bigEndian: true).ToString(), which is always lowercase, and
            //      AudioSocketServer holds those keys in a ConcurrentDictionary<string, ...> whose
            //      default comparer is ORDINAL. Only the lowercase spelling can hit it.
            //   2. Asterisk echoes `channelId` back verbatim and does not normalise it: an uppercase
            //      identifier returns HTTP 200 with Channel.Id in the spelling that was sent
            //      (RUN U), so GetStream(Channel.Id) would miss with no error on any hop.
            var identifier = Guid.NewGuid().ToString();
            data = identifier;
            channelId = identifier;
        }

        // 2. Create ExternalMedia channel via ARI
        Channel = await AriClient.Channels.CreateExternalMediaAsync(
            App, ExternalHost, Format,
            encapsulation: encapsulation,
            transport: transport,
            data: data,
            channelId: channelId,
            cancellationToken: cancellationToken);

        // 3. Poll for audio server connection (200ms interval)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectionTimeout);

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                if (_audioSocketServer is not null)
                {
                    _audioStream = _audioSocketServer.GetStream(Channel.Id);
                    if (_audioStream is not null) break;
                }

                if (_webSocketServer is not null)
                {
                    _audioStream = _webSocketServer.GetStream(Channel.Id);
                    if (_audioStream is not null) break;
                }

                await Task.Delay(200, timeoutCts.Token);
            }

            if (_audioStream is null)
                throw new TimeoutException($"Asterisk did not connect to audio server within {ConnectionTimeout}");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Asterisk did not connect to audio server within {ConnectionTimeout}");
        }
    }

    protected override async ValueTask OnCancellingAsync(CancellationToken cancellationToken)
    {
        if (Channel is not null)
            await AriClient.Channels.HangupAsync(Channel.Id, cancellationToken);

        if (_audioStream is not null)
            await _audioStream.DisposeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_audioStream is not null)
            await _audioStream.DisposeAsync();

        await base.DisposeAsync();
    }
}
