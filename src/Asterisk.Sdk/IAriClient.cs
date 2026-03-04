namespace Asterisk.Sdk;

/// <summary>
/// Represents an async client for the Asterisk REST Interface (ARI).
/// </summary>
public interface IAriClient : IAsyncDisposable
{
    /// <summary>Whether the WebSocket event connection is active.</summary>
    bool IsConnected { get; }

    /// <summary>Connect to the ARI WebSocket event stream.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnect from the ARI WebSocket event stream.</summary>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribe to ARI events via IObservable.</summary>
    IDisposable Subscribe(IObserver<AriEvent> observer);

    /// <summary>Access channel operations.</summary>
    IAriChannelsResource Channels { get; }

    /// <summary>Access bridge operations.</summary>
    IAriBridgesResource Bridges { get; }

    /// <summary>Access playback operations.</summary>
    IAriPlaybacksResource Playbacks { get; }

    /// <summary>Access recording operations.</summary>
    IAriRecordingsResource Recordings { get; }

    /// <summary>Access endpoint operations.</summary>
    IAriEndpointsResource Endpoints { get; }

    /// <summary>Access application operations.</summary>
    IAriApplicationsResource Applications { get; }

    /// <summary>Access sound operations.</summary>
    IAriSoundsResource Sounds { get; }
}

/// <summary>
/// Base class for all ARI events received via WebSocket.
/// </summary>
public class AriEvent
{
    public string? Type { get; set; }
    public string? Application { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string? RawJson { get; set; }
}

// ---------------------------------------------------------------------------
// Channel resource
// ---------------------------------------------------------------------------

/// <summary>
/// ARI channel operations.
/// </summary>
public interface IAriChannelsResource
{
    /// <summary>List all active channels.</summary>
    ValueTask<AriChannel[]> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<AriChannel> CreateAsync(string endpoint, string? app = null, CancellationToken cancellationToken = default);
    ValueTask<AriChannel> GetAsync(string channelId, CancellationToken cancellationToken = default);
    ValueTask HangupAsync(string channelId, CancellationToken cancellationToken = default);
    ValueTask<AriChannel> OriginateAsync(string endpoint, string? extension = null, string? context = null, CancellationToken cancellationToken = default);

    /// <summary>Indicate ringing to a channel. POST /channels/{channelId}/ring</summary>
    ValueTask RingAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Indicate call progress. POST /channels/{channelId}/progress</summary>
    ValueTask ProgressAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Answer a channel. POST /channels/{channelId}/answer</summary>
    ValueTask AnswerAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Create an external media channel. POST /channels/externalMedia</summary>
    ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
        string? encapsulation = null, string? transport = null, string? connectionType = null,
        string? direction = null, string? data = null, CancellationToken cancellationToken = default);

    /// <summary>Get a channel variable. GET /channels/{channelId}/variable</summary>
    ValueTask<AriVariable> GetVariableAsync(string channelId, string variable, CancellationToken cancellationToken = default);

    /// <summary>Set a channel variable. POST /channels/{channelId}/variable</summary>
    ValueTask SetVariableAsync(string channelId, string variable, string value, CancellationToken cancellationToken = default);

    /// <summary>Hold a channel. PUT /channels/{channelId}/hold</summary>
    ValueTask HoldAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Remove a channel from hold. DELETE /channels/{channelId}/hold</summary>
    ValueTask UnholdAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Mute a channel. PUT /channels/{channelId}/mute</summary>
    ValueTask MuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default);

    /// <summary>Unmute a channel. DELETE /channels/{channelId}/mute</summary>
    ValueTask UnmuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default);

    /// <summary>Send DTMF to a channel. POST /channels/{channelId}/dtmf</summary>
    ValueTask SendDtmfAsync(string channelId, string dtmf, int? before = null, int? between = null, int? duration = null, int? after = null, CancellationToken cancellationToken = default);

    /// <summary>Play media to a channel. POST /channels/{channelId}/play</summary>
    ValueTask<AriPlayback> PlayAsync(string channelId, string media, string? lang = null, int? offsetms = null, int? skipms = null, string? playbackId = null, CancellationToken cancellationToken = default);

    /// <summary>Record a channel. POST /channels/{channelId}/record</summary>
    ValueTask<AriLiveRecording> RecordAsync(string channelId, string name, string format, int? maxDurationSeconds = null, int? maxSilenceSeconds = null, string? ifExists = null, bool? beep = null, string? terminateOn = null, CancellationToken cancellationToken = default);

    /// <summary>Snoop on a channel. POST /channels/{channelId}/snoop</summary>
    ValueTask<AriChannel> SnoopAsync(string channelId, string app, string? spy = null, string? whisper = null, string? snoopId = null, CancellationToken cancellationToken = default);

    /// <summary>Redirect a channel to a different extension. POST /channels/{channelId}/redirect</summary>
    ValueTask RedirectAsync(string channelId, string endpoint, CancellationToken cancellationToken = default);

    /// <summary>Continue dialplan execution on a channel. POST /channels/{channelId}/continue</summary>
    ValueTask ContinueAsync(string channelId, string? context = null, string? extension = null, int? priority = null, string? label = null, CancellationToken cancellationToken = default);

    /// <summary>Create a channel without dialing. POST /channels/create</summary>
    ValueTask<AriChannel> CreateWithoutDialAsync(string endpoint, string app, string? channelId = null, string? otherChannelId = null, string? originator = null, IReadOnlyDictionary<string, string>? variables = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// ARI bridge operations.
/// </summary>
public interface IAriBridgesResource
{
    /// <summary>List all active bridges.</summary>
    ValueTask<AriBridge[]> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<AriBridge> CreateAsync(string? type = null, string? name = null, CancellationToken cancellationToken = default);
    ValueTask<AriBridge> GetAsync(string bridgeId, CancellationToken cancellationToken = default);
    ValueTask DestroyAsync(string bridgeId, CancellationToken cancellationToken = default);
    ValueTask AddChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default);
    ValueTask RemoveChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Playback resource
// ---------------------------------------------------------------------------

/// <summary>ARI playback operations.</summary>
public interface IAriPlaybacksResource
{
    /// <summary>Get a playback by ID. GET /playbacks/{playbackId}</summary>
    ValueTask<AriPlayback> GetAsync(string playbackId, CancellationToken cancellationToken = default);

    /// <summary>Stop a playback. DELETE /playbacks/{playbackId}</summary>
    ValueTask StopAsync(string playbackId, CancellationToken cancellationToken = default);

    /// <summary>Control a playback (e.g. pause, unpause, restart, reverse, forward). POST /playbacks/{playbackId}/control</summary>
    ValueTask ControlAsync(string playbackId, string operation, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Recording resource
// ---------------------------------------------------------------------------

/// <summary>ARI recording operations.</summary>
public interface IAriRecordingsResource
{
    /// <summary>Get a live recording by name. GET /recordings/live/{recordingName}</summary>
    ValueTask<AriLiveRecording> GetLiveAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Stop a live recording. POST /recordings/live/{recordingName}/stop</summary>
    ValueTask StopAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Delete a stored recording. DELETE /recordings/stored/{recordingName}</summary>
    ValueTask DeleteStoredAsync(string recordingName, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Endpoint resource
// ---------------------------------------------------------------------------

/// <summary>ARI endpoint operations.</summary>
public interface IAriEndpointsResource
{
    /// <summary>List all endpoints. GET /endpoints</summary>
    ValueTask<AriEndpoint[]> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Get an endpoint by technology and resource. GET /endpoints/{tech}/{resource}</summary>
    ValueTask<AriEndpoint> GetAsync(string tech, string resource, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Application resource
// ---------------------------------------------------------------------------

/// <summary>ARI application operations.</summary>
public interface IAriApplicationsResource
{
    /// <summary>List all Stasis applications. GET /applications</summary>
    ValueTask<AriApplication[]> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Get a Stasis application by name. GET /applications/{applicationName}</summary>
    ValueTask<AriApplication> GetAsync(string applicationName, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Sound resource
// ---------------------------------------------------------------------------

/// <summary>ARI sound operations.</summary>
public interface IAriSoundsResource
{
    /// <summary>List all sounds. GET /sounds</summary>
    ValueTask<AriSound[]> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Get a sound by ID. GET /sounds/{soundId}</summary>
    ValueTask<AriSound> GetAsync(string soundId, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

/// <summary>ARI channel state.</summary>
public enum AriChannelState
{
    Down,
    Rsrvd,
    OffHook,
    Dialing,
    Ring,
    Ringing,
    Up,
    Busy,
    DialingOffhook,
    PreRing,
    Unknown
}

/// <summary>ARI channel model.</summary>
public sealed class AriChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AriChannelState State { get; set; } = AriChannelState.Unknown;
    public AriCallerId? Caller { get; set; }
    public AriCallerId? Connected { get; set; }
    public string? Accountcode { get; set; }
    public AriDialplanCep? Dialplan { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset? Creationtime { get; set; }
    public string? Protocol { get; set; }
    public Dictionary<string, string>? ChannelVars { get; set; }
}

/// <summary>ARI bridge model.</summary>
public sealed class AriBridge
{
    public string Id { get; set; } = string.Empty;
    public string Technology { get; set; } = string.Empty;
    public string BridgeType { get; set; } = string.Empty;
    public IReadOnlyList<string> Channels { get; set; } = [];
}

/// <summary>ARI playback model.</summary>
public sealed class AriPlayback
{
    public string Id { get; set; } = string.Empty;
    public string MediaUri { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string TargetUri { get; set; } = string.Empty;
    public string? Language { get; set; }
}

/// <summary>ARI live recording model.</summary>
public sealed class AriLiveRecording
{
    public string Name { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? TargetUri { get; set; }
    public int? Duration { get; set; }
    public int? TalkingDuration { get; set; }
    public int? SilenceDuration { get; set; }
}

/// <summary>ARI stored recording model.</summary>
public sealed class AriStoredRecording
{
    public string Name { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}

/// <summary>ARI endpoint model.</summary>
public sealed class AriEndpoint
{
    public string Technology { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string? State { get; set; }
    public IReadOnlyList<string> ChannelIds { get; set; } = [];
}

/// <summary>ARI application model.</summary>
public sealed class AriApplication
{
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<string> ChannelIds { get; set; } = [];
    public IReadOnlyList<string> BridgeIds { get; set; } = [];
    public IReadOnlyList<string> EndpointIds { get; set; } = [];
    public IReadOnlyList<string> DeviceNames { get; set; } = [];
}

/// <summary>ARI sound model.</summary>
public sealed class AriSound
{
    public string Id { get; set; } = string.Empty;
    public string? Text { get; set; }
    public IReadOnlyList<AriFormatLang> Formats { get; set; } = [];
}

/// <summary>ARI sound format/language pair.</summary>
public sealed class AriFormatLang
{
    public string Language { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}

/// <summary>ARI caller identification.</summary>
public sealed class AriCallerId
{
    public string Name { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
}

/// <summary>ARI dialplan context/extension/priority.</summary>
public sealed class AriDialplanCep
{
    public string Context { get; set; } = string.Empty;
    public string Exten { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string? AppName { get; set; }
    public string? AppData { get; set; }
}

/// <summary>ARI channel variable.</summary>
public sealed class AriVariable
{
    public string Value { get; set; } = string.Empty;
}
