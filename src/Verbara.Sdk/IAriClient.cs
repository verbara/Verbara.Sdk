using System.Text.Json.Serialization;
using Verbara.Sdk.Enums;

namespace Verbara.Sdk;

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
    /// <remarks>
    /// <paramref name="cancellationToken"/> bounds the dial of this connect and nothing after it: cancelling it once
    /// the connect has returned does not end the connection, its events or a reconnect. End the connection with
    /// <see cref="DisconnectAsync"/> or by disposing the client.
    /// </remarks>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnect from the ARI WebSocket event stream.</summary>
    /// <remarks>
    /// Waits at most 5 s for Asterisk's answering close, or until the caller's token is cancelled.
    /// The close frame is sent before that token ends the wait. The close is best effort: an Asterisk that
    /// never answers it does not hold the call past that bound, and the connection is let go either way.
    /// </remarks>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribe to ARI events via IObservable.</summary>
    /// <remarks>
    /// What an observer throws stops neither the other observers nor the client: it is logged at Warning and counted,
    /// the event goes on to the next observer, and an observer subscribed directly stays subscribed. Only what throws on
    /// the delivering thread is caught, and a subscription composed through an Rx operator ends on its first failure.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The client was built with its events turned off (<see cref="State"/> reads
    /// <see cref="AriConnectionState.RestOnly"/>): it delivers no events, and the message names the option.
    /// </exception>
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
    /// <summary>Originate a channel.</summary>
    /// <remarks>
    /// With <paramref name="extension"/> the channel runs the dialplan, at <paramref name="extension"/> in
    /// <paramref name="context"/>, and the client's application is not sent. Without it the channel enters the client's
    /// application in Stasis. Asterisk answers <c>200</c> to an originate into an application nobody registered, and the
    /// channel is gone at once.
    /// </remarks>
    ValueTask<AriChannel> OriginateAsync(string endpoint, string? extension = null, string? context = null, CancellationToken cancellationToken = default);

    /// <summary>Indicate ringing to a channel. POST /channels/{channelId}/ring</summary>
    ValueTask RingAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Indicate call progress. POST /channels/{channelId}/progress</summary>
    ValueTask ProgressAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Answer a channel. POST /channels/{channelId}/answer</summary>
    ValueTask AnswerAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Create an external media channel. POST /channels/externalMedia</summary>
    /// <remarks>
    /// <para>
    /// <c>channelId</c> is the unique id Asterisk assigns the channel it creates; it comes back as
    /// <see cref="AriChannel.Id"/>. Asterisk spells this query parameter in <b>camelCase</b> while
    /// every sibling here is snake_case, and it ignores a parameter it does not recognise instead of
    /// rejecting it — so a misspelling is a silent no-op, not an error status. Asterisk echoes the
    /// value back verbatim and does not require it to be a UUID.
    /// </para>
    /// <para>
    /// It is a different thing from <c>data</c>. Under AudioSocket encapsulation <c>data</c> is the
    /// identification UUID Asterisk sends on the audio connection — the value an AudioSocket server
    /// keys its stream table by, and it must parse as a UUID — while <c>channelId</c> only names the
    /// ARI channel. Passing one identifier as both is what makes a stream findable by the channel id
    /// this call returned.
    /// </para>
    /// <para>
    /// Placed after <c>data</c> rather than first among the optionals, which is where
    /// <see cref="CreateWithoutDialAsync"/> puts its own <c>channelId</c>. The inconsistency is
    /// deliberate: inserting ahead of <c>encapsulation</c> would rebind every existing positional
    /// argument to a different <c>string?</c> parameter and still compile, so callers would break
    /// silently at runtime. Appending before the cancellation token breaks only callers that passed
    /// that token positionally, and that break is a compile error.
    /// </para>
    /// </remarks>
    ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
        string? encapsulation = null, string? transport = null, string? connectionType = null,
        string? direction = null, string? data = null, string? channelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Create an external media channel from a request whose parameters are named. POST /channels/externalMedia</summary>
    /// <remarks>
    /// The default body serves an implementation written before this overload existed: a request that sets neither
    /// <see cref="AriExternalMediaRequest.TransportData"/> nor an entry of <see cref="AriExternalMediaRequest.Variables"/>
    /// is forwarded to the overload above with the same values, and one that sets either throws, so an option is never
    /// silently dropped.
    /// </remarks>
    /// <param name="request">The parameters of the create.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// The implementation does not override this overload and the request sets <c>TransportData</c> or
    /// <c>Variables</c>; nothing was sent.
    /// </exception>
    ValueTask<AriChannel> CreateExternalMediaAsync(AriExternalMediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TransportData is not null)
            throw new NotSupportedException(
                $"{GetType().Name} does not implement CreateExternalMediaAsync(AriExternalMediaRequest): it cannot send TransportData, and nothing was sent.");
        if (request.Variables is { Count: > 0 })
            throw new NotSupportedException(
                $"{GetType().Name} does not implement CreateExternalMediaAsync(AriExternalMediaRequest): it cannot send Variables, and nothing was sent.");

        return CreateExternalMediaAsync(request.App, request.ExternalHost, request.Format,
            encapsulation: request.Encapsulation, transport: request.Transport, connectionType: request.ConnectionType,
            direction: request.Direction, data: request.Data, channelId: request.ChannelId,
            cancellationToken: cancellationToken);
    }

    /// <summary>Get a channel variable. GET /channels/{channelId}/variable</summary>
    ValueTask<AriVariable> GetVariableAsync(string channelId, string variable, CancellationToken cancellationToken = default);

    /// <summary>Set a channel variable. POST /channels/{channelId}/variable</summary>
    ValueTask SetVariableAsync(string channelId, string variable, string value, CancellationToken cancellationToken = default);

    /// <summary>Hold a channel. POST /channels/{channelId}/hold</summary>
    ValueTask HoldAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Remove a channel from hold. DELETE /channels/{channelId}/hold</summary>
    ValueTask UnholdAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Mute a channel. POST /channels/{channelId}/mute; a <c>null</c> direction mutes both directions.</summary>
    ValueTask MuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default);

    /// <summary>Unmute a channel. DELETE /channels/{channelId}/mute; a <c>null</c> direction unmutes both directions.</summary>
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
    /// <remarks>
    /// <paramref name="variables"/>, when it holds an entry, is sent as the JSON body <c>{"variables":{…}}</c> with
    /// <c>Content-Type: application/json</c>, and each entry is set on the created channel. No variable is written into
    /// the query. A <see langword="null"/> or empty <paramref name="variables"/> sends no body.
    /// </remarks>
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

    /// <summary>Play media to a bridge. POST /bridges/{bridgeId}/play. <c>format</c> is the Announcer channel's audio format (<c>announcer_format</c> on the wire).</summary>
    ValueTask<AriPlayback> PlayAsync(string bridgeId, string media, string? lang = null, int? offsetms = null, int? skipms = null, string? playbackId = null, string? format = null, CancellationToken cancellationToken = default);

    /// <summary>Record audio from a bridge. POST /bridges/{bridgeId}/record. <c>recordingFormat</c> is the recording's file format (<c>format</c> on the wire); <c>format</c> is the Recorder channel's audio format (<c>recorder_format</c> on the wire).</summary>
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

    /// <summary>Rotate a log channel's file. PUT /asterisk/logging/{logChannelName}/rotate</summary>
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

/// <summary>ARI channel state, as Asterisk's <c>ast_state2str</c> writes it.</summary>
/// <remarks>
/// The SDK's ARI reader maps Asterisk's spellings to these members: <c>"Dialing Offhook"</c> to
/// <see cref="DialingOffhook"/>, <c>"Pre-ring"</c> to <see cref="PreRing"/>, and both <c>"Rsrvd"</c> and the
/// schema's <c>"Rsrved"</c> to <see cref="Rsrvd"/>. A state string it has no member for, including Asterisk's
/// <c>"Unknown (N)"</c>, reads as <see cref="Unknown"/>, and the event or reply that carries it is still
/// delivered. Members keep their values; <see cref="Mute"/> was added after <see cref="Unknown"/>. The reader is
/// <see cref="AriChannelStateConverter"/>, declared on this type, so any serializer reads and writes these spellings.
/// </remarks>
[JsonConverter(typeof(AriChannelStateConverter))]
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
    Unknown,

    /// <summary>
    /// <c>"Mute"</c>, Asterisk's <c>AST_STATE_MUTE</c>. Written by channel drivers other than PJSIP (analog, DAHDI);
    /// never observed on a PJSIP channel.
    /// </summary>
    Mute,
}

/// <summary>ARI channel model.</summary>
public sealed class AriChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// The channel's state. A state string the SDK has no member for reads as <see cref="AriChannelState.Unknown"/>.
    /// </summary>
    public AriChannelState State { get; set; } = AriChannelState.Unknown;
    public AriCallerId? Caller { get; set; }
    public AriCallerId? Connected { get; set; }
    public string? Accountcode { get; set; }
    public AriDialplanCep? Dialplan { get; set; }
    public string? Language { get; set; }
    public string? Creationtime { get; set; }
    /// <summary>The channel driver's protocol id (for PJSIP, the SIP Call-ID), read from <c>protocol_id</c>.</summary>
    [JsonPropertyName("protocol_id")]
    public string? Protocol { get; set; }

    /// <summary>
    /// The channel variables <c>ari.conf</c>'s <c>channelvars</c> names, read from <c>channelvars</c>; null when none
    /// is configured.
    /// </summary>
    [JsonPropertyName("channelvars")]
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

    /// <summary>The user and group Asterisk runs as, read from <c>setid</c>.</summary>
    [JsonPropertyName("setid")]
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

/// <summary>ARI RTP statistics, as Asterisk writes a <c>GET /channels/{channelId}/rtp_statistics</c> reply.</summary>
/// <remarks>
/// Times are in seconds. The jitter, loss-deviation and round-trip figures are reals; the SSRCs and the octet
/// counts are unsigned 32-bit integers, and a far end may pick an SSRC from 2^31 up.
/// </remarks>
public sealed class AriRtpStats
{
    public int? Txcount { get; set; }
    public int? Rxcount { get; set; }
    public double? Txjitter { get; set; }
    public double? Rxjitter { get; set; }
    public int? Txploss { get; set; }
    public int? Rxploss { get; set; }

    /// <summary>The round-trip time Asterisk computed from RTCP, in seconds (<c>rtt</c>).</summary>
    public double? Rtt { get; set; }

    /// <summary>The largest round-trip time, in seconds (<c>maxrtt</c>).</summary>
    public double? Maxrtt { get; set; }

    /// <summary>The smallest round-trip time, in seconds (<c>minrtt</c>).</summary>
    public double? Minrtt { get; set; }

    /// <summary>The mean round-trip time, in seconds (<c>normdevrtt</c>).</summary>
    public double? Normdevrtt { get; set; }

    /// <summary>The standard deviation of the round-trip time, in seconds (<c>stdevrtt</c>).</summary>
    public double? Stdevrtt { get; set; }

    public string? ChannelUniqueid { get; set; }

    /// <summary>The SSRC this side sends (<c>local_ssrc</c>), an unsigned 32-bit identifier.</summary>
    public uint? LocalSsrc { get; set; }

    /// <summary>
    /// The SSRC the far end sends (<c>remote_ssrc</c>), an unsigned 32-bit identifier that may be 2^31 or more.
    /// </summary>
    public uint? RemoteSsrc { get; set; }

    /// <summary>The octets sent (<c>txoctetcount</c>).</summary>
    public uint? Txoctetcount { get; set; }

    /// <summary>The octets received (<c>rxoctetcount</c>).</summary>
    public uint? Rxoctetcount { get; set; }

    public double? LocalMaxjitter { get; set; }
    public double? LocalMinjitter { get; set; }
    public double? LocalNormdevjitter { get; set; }
    public double? LocalStdevjitter { get; set; }

    /// <summary>The largest jitter the far end reported (<c>remote_maxjitter</c>).</summary>
    public double? RemoteMaxjitter { get; set; }

    /// <summary>The smallest jitter the far end reported (<c>remote_minjitter</c>).</summary>
    public double? RemoteMinjitter { get; set; }

    /// <summary>The mean jitter the far end reported (<c>remote_normdevjitter</c>).</summary>
    public double? RemoteNormdevjitter { get; set; }

    /// <summary>The standard deviation of the jitter the far end reported (<c>remote_stdevjitter</c>).</summary>
    public double? RemoteStdevjitter { get; set; }

    /// <summary>The largest receive packet loss on this side (<c>local_maxrxploss</c>), a real.</summary>
    public double? LocalMaxrxploss { get; set; }

    /// <summary>The smallest receive packet loss on this side (<c>local_minrxploss</c>), a real.</summary>
    public double? LocalMinrxploss { get; set; }

    /// <summary>The mean receive packet loss on this side (<c>local_normdevrxploss</c>).</summary>
    public double? LocalNormdevrxploss { get; set; }

    /// <summary>The standard deviation of the receive packet loss on this side (<c>local_stdevrxploss</c>).</summary>
    public double? LocalStdevrxploss { get; set; }

    /// <summary>The largest receive packet loss the far end reported (<c>remote_maxrxploss</c>).</summary>
    public double? RemoteMaxrxploss { get; set; }

    /// <summary>The smallest receive packet loss the far end reported (<c>remote_minrxploss</c>).</summary>
    public double? RemoteMinrxploss { get; set; }

    /// <summary>The mean receive packet loss the far end reported (<c>remote_normdevrxploss</c>).</summary>
    public double? RemoteNormdevrxploss { get; set; }

    /// <summary>The standard deviation of the receive packet loss the far end reported (<c>remote_stdevrxploss</c>).</summary>
    public double? RemoteStdevrxploss { get; set; }

    /// <summary>The media experience score of what this side sends (<c>txmes</c>).</summary>
    public double? Txmes { get; set; }

    /// <summary>The media experience score of what this side receives (<c>rxmes</c>).</summary>
    public double? Rxmes { get; set; }

    /// <summary>The largest media experience score the far end reported (<c>remote_maxmes</c>).</summary>
    public double? RemoteMaxmes { get; set; }

    /// <summary>The smallest media experience score the far end reported (<c>remote_minmes</c>).</summary>
    public double? RemoteMinmes { get; set; }

    /// <summary>The mean media experience score the far end reported (<c>remote_normdevmes</c>).</summary>
    public double? RemoteNormdevmes { get; set; }

    /// <summary>The standard deviation of the media experience score the far end reported (<c>remote_stdevmes</c>).</summary>
    public double? RemoteStdevmes { get; set; }

    /// <summary>The largest media experience score on this side (<c>local_maxmes</c>).</summary>
    public double? LocalMaxmes { get; set; }

    /// <summary>The smallest media experience score on this side (<c>local_minmes</c>).</summary>
    public double? LocalMinmes { get; set; }

    /// <summary>The mean media experience score on this side (<c>local_normdevmes</c>).</summary>
    public double? LocalNormdevmes { get; set; }

    /// <summary>The standard deviation of the media experience score on this side (<c>local_stdevmes</c>).</summary>
    public double? LocalStdevmes { get; set; }
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
    /// <summary>
    /// The key this stream is registered under in its server's table. It is <b>not</b>, in general,
    /// an ARI channel id: the two implementations derive it from different things, and a value that
    /// is a valid key for one of them is meaningless to the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AudioSocket.</b> The UUID Asterisk sent in the AudioSocket identification frame, rendered
    /// from the sixteen wire bytes as <c>new Guid(bytes, bigEndian: true).ToString()</c> — so always
    /// in <b>canonical lowercase hyphenated</b> form, e.g.
    /// <c>cd98c592-9ce2-4093-afae-420d53629a4f</c>. Over ARI that is the value the creator passed as
    /// <c>data</c> to <see cref="IAriChannelsResource.CreateExternalMediaAsync(string, string, string, string?, string?, string?, string?, string?, string?, CancellationToken)"/>. It equals
    /// <see cref="AriChannel.Id"/> only when the creator passed that same value as <c>channelId</c>
    /// as well — which is what <c>ExternalMediaActivity</c> does. Passing <c>data</c> alone leaves
    /// <see cref="AriChannel.Id"/> an Asterisk-minted uniqueid such as <c>1790244226.1</c>, which
    /// appears in no stream table.
    /// </para>
    /// <para>
    /// <b>WebSocket.</b> The <c>channel_id</c> of the <c>MEDIA_START</c> chan_websocket sends as the
    /// connection's first frame, literal and compared ordinally, with no normalisation. For an
    /// <c>externalMedia</c> channel created with <c>transport=websocket</c> it is the ARI channel id
    /// the create returned (<see cref="AriChannel.Id"/>). For a call that reaches the server through
    /// <c>Dial(WebSocket/&lt;connection&gt;)</c> it is the uniqueid of the WebSocket channel, not the
    /// caller's, and the plain-text <c>MEDIA_START</c> carries no variable that correlates it with
    /// the caller. A connection whose first frame is not <c>MEDIA_START</c>, or that sends no frame
    /// within <c>AudioServerOptions.MediaStartTimeout</c> (or every connection, when that is zero),
    /// is keyed by the last segment of its HTTP upgrade request's path, query string removed: the
    /// same for every call of one <c>websocket_client</c> connection.
    /// </para>
    /// </remarks>
    string ChannelId { get; }

    /// <summary>Audio format (e.g., "slin16", "ulaw", "alaw").</summary>
    /// <remarks>
    /// For a WebSocket stream, the <c>format</c> of the first <c>MEDIA_START</c> the connection sent, or the server's
    /// <c>AudioServerOptions.DefaultFormat</c> until one is read.
    /// </remarks>
    string Format { get; }

    /// <summary>Sample rate in Hz derived from format.</summary>
    /// <remarks>
    /// For a WebSocket stream, the rate of the first <c>MEDIA_START</c>'s <c>format</c>, or of the server's
    /// <c>AudioServerOptions.DefaultFormat</c> until one is read.
    /// </remarks>
    int SampleRate { get; }

    /// <summary>Whether the stream is actively connected.</summary>
    bool IsConnected { get; }

    /// <summary>Observable for connection state changes.</summary>
    /// <remarks>
    /// The stream publishes its ending once: <see cref="AudioStreamState.Disconnected"/> exactly once, as
    /// the last state, after <see cref="AudioStreamState.Error"/> when it ended badly; a disposal after the
    /// ending publishes nothing more. What precedes <see cref="AudioStreamState.Disconnected"/> depends on the
    /// transport. An AudioSocket stream publishes <see cref="AudioStreamState.Error"/> when an error frame
    /// ended it, and a reset or other failed connection ends it as <see cref="AudioStreamState.Disconnected"/>
    /// alone, as a hangup does. A WebSocket stream publishes <see cref="AudioStreamState.Error"/> when its
    /// connection failed while it was live: a reset, or an end with no close frame from the peer. A close
    /// frame from the peer, whatever its status, and an ending on this side (a disposal or the server's stop)
    /// publish <see cref="AudioStreamState.Disconnected"/> alone, even when the connection fails while that
    /// ending is in progress. A subscriber is replayed the current state inside its
    /// <c>Subscribe</c> call, and an exception it throws there reaches that caller, with the subscription
    /// dropped. An exception a subscriber throws on a later notification is logged at Error and does not
    /// stop the other subscribers, the stream's teardown or its release by the server that owns it, which
    /// may already have released the stream when a subscriber is notified that it ended.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Each stream is delivered <b>synchronously</b> to the observers, in the order they subscribed, from the
    /// handler of the connection that carries it: after that connection has been admitted and is listed in
    /// <see cref="ActiveStreams"/> and counted in <see cref="ActiveStreamCount"/>, and never on the server's
    /// accept loop, so other connections are accepted and served while an observer runs. Being announced does
    /// not mean being found: a connection that presents a key another live connection holds is announced while
    /// it waits for that key, and <see cref="GetStream(string)"/> keeps returning the earlier holder until it
    /// takes the key over.
    /// </para>
    /// <para>
    /// An observer that <b>blocks</b> holds that connection's release — its registration, its place in the
    /// server's concurrent-stream limit and its socket — until it returns, even after the far end has hung up.
    /// What a stop with no cancellable token does meanwhile depends on the server, and each one states it on its
    /// own <c>OnStreamConnected</c>: <c>WebSocketAudioServer.StopAsync</c> waits for that observer to return.
    /// <c>AudioSocketServer.StopAsync</c> does not wait for it: it ends that stream, removes its registration and
    /// returns while the observer still runs, and the connection's place in the limit comes back only when the
    /// observer returns.
    /// </para>
    /// <para>
    /// An observer that <b>throws</b> loses that connection: the server logs the failure, releases the
    /// connection and closes it, and observers subscribed after the throwing one do not receive that stream.
    /// </para>
    /// <para>
    /// Long or blocking work belongs off the observer: hand the stream to <see cref="Task.Run(Func{Task})"/>,
    /// or to a channel the consumer drains, and return.
    /// </para>
    /// </remarks>
    IObservable<IAudioStream> OnStreamConnected { get; }

    /// <summary>
    /// Get an active stream by the key its server registered it under. Each implementation states
    /// what that key is, in what form, and where a caller obtains it; see the parameter.
    /// </summary>
    /// <param name="channelId">
    /// The registration key. It is the stream's <see cref="IAudioStream.ChannelId"/> and is
    /// described in full there; the parameter keeps its old name for source compatibility. In short
    /// — for <c>AudioSocketServer</c>: the UUID from Asterisk's AudioSocket identification frame, in
    /// canonical lowercase hyphenated form and compared <b>ordinally</b>, so any other spelling of
    /// the same UUID misses; an ARI <see cref="AriChannel.Id"/> is that key only when the channel
    /// was created with that same value in both <c>channelId</c> and <c>data</c>, as
    /// <c>ExternalMediaActivity</c> does. For <c>WebSocketAudioServer</c>: the literal
    /// <c>channel_id</c> of the connection's <c>MEDIA_START</c>, compared ordinally — for an
    /// <c>externalMedia</c> channel the <see cref="AriChannel.Id"/> the create returned, for a
    /// <c>Dial(WebSocket/…)</c> the WebSocket channel's uniqueid; a connection that names no call
    /// is keyed by its upgrade path's last segment, and a connection that names the call outranks
    /// one that only presents the same value as its path segment.
    /// </param>
    /// <returns>
    /// The live stream registered under that key, or <see langword="null"/> when none is. An
    /// aggregate over several servers (<c>CompositeAudioServer</c>) returns the first stream any of
    /// them holds under the key, and its <see langword="null"/> means that none of them holds a live
    /// stream under it; a found stream says its transport by its type (a WebSocket stream is an
    /// <c>IChanWebSocketSession</c>).
    /// </returns>
    IAudioStream? GetStream(string channelId);

    /// <summary>All currently active audio streams.</summary>
    IEnumerable<IAudioStream> ActiveStreams { get; }

    /// <summary>Number of currently active audio streams.</summary>
    int ActiveStreamCount { get; }
}
