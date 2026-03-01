using System.Buffers;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Ari.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.NetAot.Ari.Client;

internal static partial class AriClientLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "ARI connected to {BaseUrl} app={Application}")]
    public static partial void Connected(ILogger logger, string baseUrl, string application);

    [LoggerMessage(Level = LogLevel.Information, Message = "ARI disconnected")]
    public static partial void Disconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ARI event received: {EventType}")]
    public static partial void EventReceived(ILogger logger, string? eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "ARI WebSocket error")]
    public static partial void WebSocketError(ILogger logger, Exception exception);
}

/// <summary>
/// ARI client implementation using HttpClient for REST and ClientWebSocket for events.
/// </summary>
public sealed class AriClient : IAriClient
{
    private readonly AriClientOptions _options;
    private readonly ILogger<AriClient> _logger;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _eventLoop;
    private readonly Subject<AriEvent> _eventSubject = new();

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public IAriChannelsResource Channels { get; }
    public IAriBridgesResource Bridges { get; }

    public AriClient(IOptions<AriClientOptions> options, ILogger<AriClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        _httpClient = new HttpClient { BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/ari/") };
        var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        Channels = new AriChannelsResource(_httpClient, _options);
        Bridges = new AriBridgesResource(_httpClient, _options);
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();

        var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        _webSocket.Options.SetRequestHeader("Authorization",
            "Basic " + Convert.ToBase64String(authBytes));

        var wsUrl = _options.BaseUrl.TrimEnd('/')
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{wsUrl}/ari/events?api_key={_options.Username}:{_options.Password}&app={_options.Application}");

        await _webSocket.ConnectAsync(uri, cancellationToken);
        _eventLoop = Task.Run(() => EventLoopAsync(_cts.Token), CancellationToken.None);

        AriClientLog.Connected(_logger, _options.BaseUrl, _options.Application);
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(buffer.AsMemory(), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var evt = ParseEvent(json);
                    if (evt is not null)
                    {
                        AriClientLog.EventReceived(_logger, evt.Type);
                        _eventSubject.OnNext(evt);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            AriClientLog.WebSocketError(_logger, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AriEvent? ParseEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AriEvent
            {
                Type = root.TryGetProperty("type", out var t) ? t.GetString() : null,
                Application = root.TryGetProperty("application", out var a) ? a.GetString() : null,
                Timestamp = root.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), out var dto) ? dto : null,
                RawJson = json
            };
        }
        catch
        {
            return null;
        }
    }

    public IDisposable Subscribe(IObserver<AriEvent> observer) => _eventSubject.Subscribe(observer);

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) await _cts.CancelAsync();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
            }
            catch { /* Best effort */ }
        }

        if (_eventLoop is not null)
            await _eventLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        AriClientLog.Disconnected(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected) await DisconnectAsync();
        _eventSubject.OnCompleted();
        _eventSubject.Dispose();
        _webSocket?.Dispose();
        _httpClient.Dispose();
        _cts?.Dispose();
    }
}
