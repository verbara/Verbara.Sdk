using System.Reactive.Subjects;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Audio;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Activity that creates an ExternalMedia channel, waits for Asterisk
/// to connect back via AudioSocket or WebSocket, and provides an IAudioStream
/// for bidirectional audio streaming.
/// </summary>
public sealed class ExternalMediaActivity : IActivity
{
    private readonly IAriClient _ariClient;
    private readonly AudioSocketServer? _audioSocketServer;
    private readonly WebSocketAudioServer? _webSocketServer;
    private readonly BehaviorSubject<ActivityStatus> _statusSubject = new(ActivityStatus.Pending);
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

    public ActivityStatus Status => _statusSubject.Value;
    public IObservable<ActivityStatus> StatusChanges => _statusSubject;

    public ExternalMediaActivity(IAriClient ariClient, AudioSocketServer? audioSocketServer = null, WebSocketAudioServer? webSocketServer = null)
    {
        _ariClient = ariClient;
        _audioSocketServer = audioSocketServer;
        _webSocketServer = webSocketServer;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _statusSubject.OnNext(ActivityStatus.Starting);
        try
        {
            // 1. Create ExternalMedia channel via ARI
            Channel = await _ariClient.Channels.CreateExternalMediaAsync(
                App, ExternalHost, Format,
                encapsulation: Encapsulation,
                transport: Transport,
                cancellationToken: cancellationToken);

            _statusSubject.OnNext(ActivityStatus.InProgress);

            // 2. Wait for Asterisk to connect to audio server
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectionTimeout);

            while (_audioStream is null && !timeoutCts.Token.IsCancellationRequested)
            {
                // Check AudioSocket server
                if (_audioSocketServer is not null)
                {
                    _audioStream = _audioSocketServer.GetStream(Channel.Id);
                    if (_audioStream is not null) break;
                }

                // Check WebSocket server
                if (_webSocketServer is not null)
                {
                    _audioStream = _webSocketServer.GetStream(Channel.Id);
                    if (_audioStream is not null) break;
                }

                await Task.Delay(50, timeoutCts.Token);
            }

            if (_audioStream is null)
            {
                _statusSubject.OnNext(ActivityStatus.Failed);
                throw new TimeoutException($"Asterisk did not connect to audio server within {ConnectionTimeout}");
            }

            // 3. Stream is ready
        }
        catch (OperationCanceledException)
        {
            _statusSubject.OnNext(ActivityStatus.Cancelled);
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            _statusSubject.OnNext(ActivityStatus.Failed);
            throw;
        }
    }

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        if (Channel is not null)
            await _ariClient.Channels.HangupAsync(Channel.Id, cancellationToken);

        if (_audioStream is not null)
            await _audioStream.DisposeAsync();

        _statusSubject.OnNext(ActivityStatus.Cancelled);
    }

    public async ValueTask DisposeAsync()
    {
        if (_audioStream is not null)
            await _audioStream.DisposeAsync();
        _statusSubject.OnCompleted();
        _statusSubject.Dispose();
    }
}
