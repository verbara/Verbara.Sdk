using Asterisk.Sdk.Enums;

namespace Asterisk.Sdk;

/// <summary>
/// Represents an async client for the Asterisk REST Interface (ARI).
/// </summary>
public interface IAriClient : IAsyncDisposable
{
    /// <summary>Current connection state.</summary>
    AriConnectionState State { get; }

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

    /// <summary>Access device state operations.</summary>
    IAriDeviceStatesResource DeviceStates { get; }

    /// <summary>Access Asterisk system operations.</summary>
    IAriAsteriskResource Asterisk { get; }

    /// <summary>Access mailbox operations.</summary>
    IAriMailboxesResource Mailboxes { get; }

    /// <summary>Generate a user event. POST /events/user/{eventName}</summary>
    ValueTask GenerateUserEventAsync(string eventName, string application, string? source = null, CancellationToken cancellationToken = default);

    /// <summary>Access the audio server (null if not configured).</summary>
    IAudioServer? AudioServer { get; }
}

/// <summary>
/// Base class for all ARI events received via WebSocket.
/// </summary>
public class AriEvent
{
    public string? Type { get; set; }
    public string? Application { get; set; }
    public string? Timestamp { get; set; }
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

    /// <summary>Move channel to another Stasis app. POST /channels/{channelId}/move</summary>
    ValueTask MoveAsync(string channelId, string app, string? appArgs = null, CancellationToken cancellationToken = default);

    /// <summary>Dial a created channel. POST /channels/{channelId}/dial</summary>
    ValueTask DialAsync(string channelId, string? caller = null, int? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Get RTP statistics. GET /channels/{channelId}/rtp_statistics</summary>
    ValueTask<AriRtpStats> GetRtpStatisticsAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Start silence on channel. POST /channels/{channelId}/silence</summary>
    ValueTask SilenceAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Stop silence on channel. DELETE /channels/{channelId}/silence</summary>
    ValueTask StopSilenceAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Start MOH on channel. POST /channels/{channelId}/moh</summary>
    ValueTask StartMohAsync(string channelId, string? mohClass = null, CancellationToken cancellationToken = default);

    /// <summary>Stop MOH on channel. DELETE /channels/{channelId}/moh</summary>
    ValueTask StopMohAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Stop ringing on channel. DELETE /channels/{channelId}/ring</summary>
    ValueTask StopRingAsync(string channelId, CancellationToken cancellationToken = default);
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

    /// <summary>Play media to a bridge. POST /bridges/{bridgeId}/play. The format parameter (Asterisk 23+) sets the Announcer channel audio format.</summary>
    ValueTask<AriPlayback> PlayAsync(string bridgeId, string media, string? lang = null, int? offsetms = null, int? skipms = null, string? playbackId = null, string? format = null, CancellationToken cancellationToken = default);

    /// <summary>Record audio from a bridge. POST /bridges/{bridgeId}/record. The format parameter (Asterisk 23+) sets the Recorder channel audio format.</summary>
    ValueTask<AriLiveRecording> RecordAsync(string bridgeId, string name, string recordingFormat, int? maxDurationSeconds = null, int? maxSilenceSeconds = null, string? ifExists = null, bool? beep = null, string? terminateOn = null, string? format = null, CancellationToken cancellationToken = default);

    /// <summary>Create bridge with specific ID. POST /bridges/{bridgeId}</summary>
    ValueTask<AriBridge> CreateWithIdAsync(string bridgeId, string? type = null, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>Set video source for bridge. POST /bridges/{bridgeId}/videoSource/{channelId}</summary>
    ValueTask SetVideoSourceAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default);

    /// <summary>Clear video source for bridge. DELETE /bridges/{bridgeId}/videoSource</summary>
    ValueTask ClearVideoSourceAsync(string bridgeId, CancellationToken cancellationToken = default);

    /// <summary>Start MOH on bridge. POST /bridges/{bridgeId}/moh</summary>
    ValueTask StartMohAsync(string bridgeId, string? mohClass = null, CancellationToken cancellationToken = default);

    /// <summary>Stop MOH on bridge. DELETE /bridges/{bridgeId}/moh</summary>
    ValueTask StopMohAsync(string bridgeId, CancellationToken cancellationToken = default);
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

    /// <summary>List stored recordings. GET /recordings/stored</summary>
    ValueTask<AriStoredRecording[]> ListStoredAsync(CancellationToken cancellationToken = default);

    /// <summary>Get stored recording. GET /recordings/stored/{recordingName}</summary>
    ValueTask<AriStoredRecording> GetStoredAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Copy stored recording. POST /recordings/stored/{recordingName}/copy</summary>
    ValueTask<AriStoredRecording> CopyStoredAsync(string recordingName, string destinationRecordingName, CancellationToken cancellationToken = default);

    /// <summary>Cancel live recording (discard). DELETE /recordings/live/{recordingName}</summary>
    ValueTask CancelAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Pause live recording. POST /recordings/live/{recordingName}/pause</summary>
    ValueTask PauseAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Unpause live recording. DELETE /recordings/live/{recordingName}/pause</summary>
    ValueTask UnpauseAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Mute live recording. POST /recordings/live/{recordingName}/mute</summary>
    ValueTask MuteAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Unmute live recording. DELETE /recordings/live/{recordingName}/mute</summary>
    ValueTask UnmuteAsync(string recordingName, CancellationToken cancellationToken = default);

    /// <summary>Get stored recording file (binary). GET /recordings/stored/{recordingName}/file</summary>
    ValueTask<Stream> GetStoredFileAsync(string recordingName, CancellationToken cancellationToken = default);
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

    /// <summary>List endpoints by technology. GET /endpoints/{tech}</summary>
    ValueTask<AriEndpoint[]> ListByTechAsync(string tech, CancellationToken cancellationToken = default);

    /// <summary>Send message to endpoint. PUT /endpoints/sendMessage</summary>
    ValueTask SendMessageAsync(string destination, string from, string? body = null, CancellationToken cancellationToken = default);

    /// <summary>Send message to specific endpoint. PUT /endpoints/{tech}/{resource}/sendMessage</summary>
    ValueTask SendMessageToEndpointAsync(string tech, string resource, string from, string? body = null, CancellationToken cancellationToken = default);
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

    /// <summary>Subscribe to event source. POST /applications/{applicationName}/subscription</summary>
    ValueTask<AriApplication> SubscribeAsync(string applicationName, string eventSource, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribe from event source. DELETE /applications/{applicationName}/subscription</summary>
    ValueTask<AriApplication> UnsubscribeAsync(string applicationName, string eventSource, CancellationToken cancellationToken = default);

    /// <summary>Filter application events. PUT /applications/{applicationName}/eventFilter</summary>
    ValueTask<AriApplication> SetEventFilterAsync(string applicationName, CancellationToken cancellationToken = default);
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
// Device state resource
// ---------------------------------------------------------------------------

/// <summary>ARI device state operations.</summary>
public interface IAriDeviceStatesResource
{
    /// <summary>List all device states. GET /deviceStates</summary>
    ValueTask<AriDeviceState[]> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Get a device state by name. GET /deviceStates/{deviceName}</summary>
    ValueTask<AriDeviceState> GetAsync(string deviceName, CancellationToken cancellationToken = default);

    /// <summary>Set or create a device state. PUT /deviceStates/{deviceName}</summary>
    ValueTask UpdateAsync(string deviceName, string deviceState, CancellationToken cancellationToken = default);

    /// <summary>Delete a custom device state. DELETE /deviceStates/{deviceName}</summary>
    ValueTask DeleteAsync(string deviceName, CancellationToken cancellationToken = default);
}

/// <summary>ARI Asterisk resource — system info, modules, logging, config, variables.</summary>
public interface IAriAsteriskResource
{
    ValueTask<AriAsteriskInfo> GetInfoAsync(CancellationToken cancellationToken = default);
    ValueTask<AriAsteriskPing> PingAsync(CancellationToken cancellationToken = default);
    ValueTask<string> GetVariableAsync(string variable, CancellationToken cancellationToken = default);
    ValueTask SetVariableAsync(string variable, string value, CancellationToken cancellationToken = default);
    ValueTask<AriModule[]> ListModulesAsync(CancellationToken cancellationToken = default);
    ValueTask<AriModule> GetModuleAsync(string moduleName, CancellationToken cancellationToken = default);
    ValueTask LoadModuleAsync(string moduleName, CancellationToken cancellationToken = default);
    ValueTask UnloadModuleAsync(string moduleName, CancellationToken cancellationToken = default);
    ValueTask ReloadModuleAsync(string moduleName, CancellationToken cancellationToken = default);
    ValueTask<AriLogChannel[]> ListLoggingAsync(CancellationToken cancellationToken = default);
    ValueTask AddLogChannelAsync(string logChannelName, string configuration, CancellationToken cancellationToken = default);
    ValueTask DeleteLogChannelAsync(string logChannelName, CancellationToken cancellationToken = default);
    ValueTask RotateLogChannelAsync(string logChannelName, CancellationToken cancellationToken = default);
    ValueTask<AriConfigTuple[]> GetConfigAsync(string configClass, string objectType, string id, CancellationToken cancellationToken = default);
    ValueTask UpdateConfigAsync(string configClass, string objectType, string id, CancellationToken cancellationToken = default);
    ValueTask DeleteConfigAsync(string configClass, string objectType, string id, CancellationToken cancellationToken = default);
}

/// <summary>ARI Mailboxes resource — mailbox state management.</summary>
public interface IAriMailboxesResource
{
    ValueTask<AriMailbox[]> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<AriMailbox> GetAsync(string mailboxName, CancellationToken cancellationToken = default);
    ValueTask UpdateAsync(string mailboxName, int oldMessages, int newMessages, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string mailboxName, CancellationToken cancellationToken = default);
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
    public string? Creationtime { get; set; }
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

/// <summary>ARI device state model.</summary>
public sealed class AriDeviceState
{
    public string? Name { get; set; }
    public string? State { get; set; }
}

/// <summary>ARI contact info model.</summary>
public sealed class AriContactInfo
{
    public string? Uri { get; set; }
    public string? ContactStatus { get; set; }
    public string? Aor { get; set; }
    public string? RoundtripUsec { get; set; }
}

/// <summary>ARI peer model.</summary>
public sealed class AriPeer
{
    public string? PeerStatus { get; set; }
    public string? Cause { get; set; }
    public string? Address { get; set; }
    public string? Port { get; set; }
    public string? Time { get; set; }
}

/// <summary>ARI text message model.</summary>
public sealed class AriTextMessage
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Body { get; set; }
    public Dictionary<string, string>? Variables { get; set; }
}

/// <summary>ARI Asterisk system information.</summary>
public sealed class AriAsteriskInfo
{
    public AriBuildInfo? Build { get; set; }
    public AriSystemInfo? System { get; set; }
    public AriConfigInfo? Config { get; set; }
    public AriStatusInfo? Status { get; set; }
}

/// <summary>ARI build information.</summary>
public sealed class AriBuildInfo
{
    public string Os { get; set; } = string.Empty;
    public string Kernel { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string Options { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
}

/// <summary>ARI system information.</summary>
public sealed class AriSystemInfo
{
    public string Version { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
}

/// <summary>ARI configuration information.</summary>
public sealed class AriConfigInfo
{
    public string Name { get; set; } = string.Empty;
    public string DefaultLanguage { get; set; } = string.Empty;
    public double? MaxChannels { get; set; }
    public double? MaxOpenFiles { get; set; }
    public double? MaxLoad { get; set; }
    public AriSetId? SetId { get; set; }
}

/// <summary>ARI set ID (user/group).</summary>
public sealed class AriSetId
{
    public string User { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
}

/// <summary>ARI status information.</summary>
public sealed class AriStatusInfo
{
    public string? StartupTime { get; set; }
    public string? LastReloadTime { get; set; }
}

/// <summary>ARI ping response.</summary>
public sealed class AriAsteriskPing
{
    public string? AsteriskId { get; set; }
    public string? Ping { get; set; }
    public string? Timestamp { get; set; }
}

/// <summary>ARI log channel.</summary>
public sealed class AriLogChannel
{
    public string Channel { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
}

/// <summary>ARI module information.</summary>
public sealed class AriModule
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int UseCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SupportLevel { get; set; } = string.Empty;
}

/// <summary>ARI mailbox model.</summary>
public sealed class AriMailbox
{
    public string Name { get; set; } = string.Empty;
    public int OldMessages { get; set; }
    public int NewMessages { get; set; }
}

/// <summary>ARI configuration key-value tuple.</summary>
public sealed class AriConfigTuple
{
    public string Attribute { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>ARI RTP statistics.</summary>
public sealed class AriRtpStats
{
    public int? Txcount { get; set; }
    public int? Rxcount { get; set; }
    public double? Txjitter { get; set; }
    public double? Rxjitter { get; set; }
    public int? Txploss { get; set; }
    public int? Rxploss { get; set; }
    public int? Rtt { get; set; }
    public string? ChannelUniqueid { get; set; }
    public int? LocalSsrc { get; set; }
    public int? RemoteSsrc { get; set; }
    public double? LocalMaxjitter { get; set; }
    public double? LocalMinjitter { get; set; }
    public double? LocalNormdevjitter { get; set; }
    public double? LocalStdevjitter { get; set; }
    public int? LocalMaxrxploss { get; set; }
    public int? LocalMinrxploss { get; set; }
}

// ---------------------------------------------------------------------------
// Audio streaming
// ---------------------------------------------------------------------------

/// <summary>
/// Represents a bidirectional audio stream from Asterisk.
/// Implemented by both AudioSocketSession and WebSocketAudioSession.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "IAudioStream is the correct domain name for this abstraction")]
public interface IAudioStream : IAsyncDisposable
{
    /// <summary>Unique ID of the external media channel in Asterisk.</summary>
    string ChannelId { get; }

    /// <summary>Audio format (e.g., "slin16", "ulaw", "alaw").</summary>
    string Format { get; }

    /// <summary>Sample rate in Hz derived from format.</summary>
    int SampleRate { get; }

    /// <summary>Whether the stream is actively connected.</summary>
    bool IsConnected { get; }

    /// <summary>Observable for connection state changes.</summary>
    IObservable<AudioStreamState> StateChanges { get; }

    /// <summary>Read the next audio frame. Returns empty when stream ends.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>Write an audio frame to Asterisk.</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);
}

/// <summary>Audio stream connection state.</summary>
public enum AudioStreamState
{
    Connecting,
    Connected,
    Disconnected,
    Error
}

/// <summary>Unified audio server interface (AudioSocket + WebSocket).</summary>
public interface IAudioServer
{
    /// <summary>Observable that emits each new audio stream when a connection is established.</summary>
    IObservable<IAudioStream> OnStreamConnected { get; }

    /// <summary>Get an active stream by channel ID.</summary>
    IAudioStream? GetStream(string channelId);

    /// <summary>All currently active audio streams.</summary>
    IEnumerable<IAudioStream> ActiveStreams { get; }

    /// <summary>Number of currently active audio streams.</summary>
    int ActiveStreamCount { get; }
}
