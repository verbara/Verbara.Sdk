using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Audio;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Activity that creates an ExternalMedia channel, waits for Asterisk
/// to connect back via AudioSocket or WebSocket, and provides an IAudioStream
/// for bidirectional audio streaming.
/// </summary>
public sealed class ExternalMediaActivity : AriActivityBase
{
    private readonly AudioSocketServer? _audioSocketServer;
    private readonly WebSocketAudioServer? _webSocketServer;
    private IAudioStream? _audioStream;

    /// <summary>Stasis application name.</summary>
    public required string App { get; init; }

    /// <summary>External host address for the audio connection (e.g., "192.168.1.100:9092").</summary>
    public required string ExternalHost { get; init; }

    /// <summary>Audio format. Default: "slin16".</summary>
    public string Format { get; init; } = "slin16";

    /// <summary>Encapsulation type (e.g., "audiosocket") or null for default.</summary>
    public string? Encapsulation { get; init; }

    /// <summary>Transport type (e.g., "websocket") or null for default.</summary>
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
        // 1. Create ExternalMedia channel via ARI
        Channel = await AriClient.Channels.CreateExternalMediaAsync(
            App, ExternalHost, Format,
            encapsulation: Encapsulation,
            transport: Transport,
            cancellationToken: cancellationToken);

        // 2. Poll for audio server connection (200ms interval)
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
