using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Events;

/// <summary>StasisStart - channel entered a Stasis application.</summary>
public sealed class StasisStartEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string[]? Args { get; set; }
}

/// <summary>StasisEnd - channel left a Stasis application.</summary>
public sealed class StasisEndEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelStateChange - channel state changed.</summary>
public sealed class ChannelStateChangeEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelDtmfReceived - DTMF digit received.</summary>
public sealed class ChannelDtmfReceivedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? Digit { get; set; }
    public int? DurationMs { get; set; }
}

/// <summary>ChannelHangupRequest - hangup requested.</summary>
public sealed class ChannelHangupRequestEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public int? Cause { get; set; }
    /// <summary>Technology-specific cause code (e.g. SIP response code). Asterisk 22.7+/23+.</summary>
    public string? TechCause { get; set; }
}

/// <summary>BridgeCreated - bridge was created.</summary>
public sealed class BridgeCreatedEvent : AriEvent
{
    public AriBridge? Bridge { get; set; }
}

/// <summary>BridgeDestroyed - bridge was destroyed.</summary>
public sealed class BridgeDestroyedEvent : AriEvent
{
    public AriBridge? Bridge { get; set; }
}

/// <summary>ChannelEnteredBridge - channel joined a bridge.</summary>
public sealed class ChannelEnteredBridgeEvent : AriEvent
{
    public AriBridge? Bridge { get; set; }
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelLeftBridge - channel left a bridge.</summary>
public sealed class ChannelLeftBridgeEvent : AriEvent
{
    public AriBridge? Bridge { get; set; }
    public AriChannel? Channel { get; set; }
}

/// <summary>PlaybackStarted - playback started on a channel.</summary>
public sealed class PlaybackStartedEvent : AriEvent
{
    public AriPlayback? Playback { get; set; }
}

/// <summary>PlaybackFinished - playback completed.</summary>
public sealed class PlaybackFinishedEvent : AriEvent
{
    public AriPlayback? Playback { get; set; }
}

/// <summary>Dial - a dial operation occurred.</summary>
public sealed class DialEvent : AriEvent
{
    public AriChannel? Peer { get; set; }
    public AriChannel? Caller { get; set; }
    public string? Dialstatus { get; set; }
}

/// <summary>ChannelToneDetected - tone detected on channel (Asterisk 22+).</summary>
public sealed class ChannelToneDetectedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelCreated - a new channel was created.</summary>
public sealed class ChannelCreatedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelDestroyed - a channel was destroyed.</summary>
public sealed class ChannelDestroyedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public int? Cause { get; set; }
    public string? CauseTxt { get; set; }
    /// <summary>Technology-specific cause code (e.g. SIP response code). Asterisk 22.7+/23+.</summary>
    public string? TechCause { get; set; }
}

/// <summary>ChannelVarset - a channel variable was set.</summary>
public sealed class ChannelVarsetEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

/// <summary>ChannelHold - a channel was placed on hold.</summary>
public sealed class ChannelHoldEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? MusicClass { get; set; }
}

/// <summary>ChannelUnhold - a channel was removed from hold.</summary>
public sealed class ChannelUnholdEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelTalkingStarted - talking was detected on a channel.</summary>
public sealed class ChannelTalkingStartedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>ChannelTalkingFinished - talking stopped on a channel.</summary>
public sealed class ChannelTalkingFinishedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public int? Duration { get; set; }
}

/// <summary>ChannelConnectedLine - channel connected line information changed.</summary>
public sealed class ChannelConnectedLineEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>RecordingStarted - a recording has started.</summary>
public sealed class RecordingStartedEvent : AriEvent
{
    public AriLiveRecording? Recording { get; set; }
}

/// <summary>RecordingFinished - a recording has finished.</summary>
public sealed class RecordingFinishedEvent : AriEvent
{
    public AriLiveRecording? Recording { get; set; }
}

/// <summary>EndpointStateChange - an endpoint's state changed.</summary>
public sealed class EndpointStateChangeEvent : AriEvent
{
    public AriEndpoint? Endpoint { get; set; }
}

// ---------------------------------------------------------------------------
// Sprint 1 — Transfer and recording events (Asterisk 12+)
// ---------------------------------------------------------------------------

/// <summary>BridgeAttendedTransfer - an attended transfer was completed.</summary>
public sealed class BridgeAttendedTransferEvent : AriEvent
{
    public string? Result { get; set; }
    public AriChannel? TransfererFirstLeg { get; set; }
    public AriBridge? TransfererFirstLegBridge { get; set; }
    public AriChannel? TransfererSecondLeg { get; set; }
    public AriBridge? TransfererSecondLegBridge { get; set; }
    public AriChannel? Transferee { get; set; }
    public AriChannel? TransferTarget { get; set; }
    public string? DestinationType { get; set; }
    public string? DestinationBridge { get; set; }
    public string? DestinationApplication { get; set; }
    public AriChannel? DestinationLinkFirstLeg { get; set; }
    public AriChannel? DestinationLinkSecondLeg { get; set; }
    public AriChannel? DestinationThreewayChannel { get; set; }
    public AriBridge? DestinationThreewayBridge { get; set; }
    public bool IsExternal { get; set; }
    public AriChannel? ReplaceChannel { get; set; }
}

/// <summary>BridgeBlindTransfer - a blind transfer was completed.</summary>
public sealed class BridgeBlindTransferEvent : AriEvent
{
    public string? Result { get; set; }
    public AriChannel? Transferer { get; set; }
    public AriBridge? Bridge { get; set; }
    public AriChannel? Transferee { get; set; }
    public AriChannel? ReplaceChannel { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
    public bool IsExternal { get; set; }
}

/// <summary>ChannelTransfer - a transfer was initiated (Asterisk 21+).</summary>
public sealed class ChannelTransferEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}

/// <summary>BridgeMerged - two bridges were merged.</summary>
public sealed class BridgeMergedEvent : AriEvent
{
    public AriBridge? Bridge { get; set; }
    public AriBridge? BridgeFrom { get; set; }
}

/// <summary>BridgeVideoSourceChanged - video source changed in bridge.</summary>
public sealed class BridgeVideoSourceChangedEvent : AriEvent
{
    public AriBridge? Bridge { get; set; }
    public string? OldVideoSourceId { get; set; }
}

/// <summary>RecordingFailed - a recording has failed.</summary>
public sealed class RecordingFailedEvent : AriEvent
{
    public AriLiveRecording? Recording { get; set; }
}

// ---------------------------------------------------------------------------
// Sprint 3 — Complementary ARI events (Asterisk 12-22+)
// ---------------------------------------------------------------------------

/// <summary>ChannelCallerId - caller ID changed on a channel.</summary>
public sealed class ChannelCallerIdEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public int? CallerPresentation { get; set; }
    public string? CallerPresentationTxt { get; set; }
}

/// <summary>ChannelDialplan - channel entered a new dialplan location.</summary>
public sealed class ChannelDialplanEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? DialplanApp { get; set; }
    public string? DialplanAppData { get; set; }
}

/// <summary>ChannelUserevent - user-defined event from dialplan.</summary>
public sealed class ChannelUsereventEvent : AriEvent
{
    public string? Eventname { get; set; }
    public AriChannel? Channel { get; set; }
    public AriBridge? Bridge { get; set; }
    public AriEndpoint? Endpoint { get; set; }
}

/// <summary>DeviceStateChanged - a device state changed.</summary>
public sealed class DeviceStateChangedEvent : AriEvent
{
    public AriDeviceState? DeviceState { get; set; }
}

/// <summary>PlaybackContinuing - playback continuing to next media URI.</summary>
public sealed class PlaybackContinuingEvent : AriEvent
{
    public AriPlayback? Playback { get; set; }
}

/// <summary>ContactStatusChange - endpoint contact status changed (Asterisk 13+).</summary>
public sealed class ContactStatusChangeEvent : AriEvent
{
    public AriContactInfo? ContactInfo { get; set; }
    public AriEndpoint? Endpoint { get; set; }
}

/// <summary>PeerStatusChange - peer status changed (Asterisk 13+).</summary>
public sealed class PeerStatusChangeEvent : AriEvent
{
    public AriPeer? Peer { get; set; }
    public AriEndpoint? Endpoint { get; set; }
}

/// <summary>TextMessageReceived - out-of-call text message received (Asterisk 12+).</summary>
public sealed class TextMessageReceivedEvent : AriEvent
{
    public AriTextMessage? Message { get; set; }
    public AriEndpoint? Endpoint { get; set; }
}

// ---------------------------------------------------------------------------
// Sprint 5 — ARI events for Asterisk 16-22+ and missing standard events
// ---------------------------------------------------------------------------

/// <summary>ApplicationReplaced - another WebSocket took over this application's subscription (Asterisk 12+).</summary>
public sealed class ApplicationReplacedEvent : AriEvent;

/// <summary>ApplicationMoveFailed - application move between Stasis apps failed (Asterisk 16+).</summary>
public sealed class ApplicationMoveFailedEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? Destination { get; set; }
    public string[]? Args { get; set; }
}

/// <summary>ApplicationRegistered - Stasis application registered (Asterisk 21+).</summary>
public sealed class ApplicationRegisteredEvent : AriEvent
{
    public AriApplication? RegisteredApplication { get; set; }
}

/// <summary>ApplicationUnregistered - Stasis application unregistered (Asterisk 21+).</summary>
public sealed class ApplicationUnregisteredEvent : AriEvent
{
    public AriApplication? UnregisteredApplication { get; set; }
}

/// <summary>MissingParams - required parameters missing for ARI request (Asterisk 12+).</summary>
public sealed class MissingParamsEvent : AriEvent
{
    public string[]? Params { get; set; }
}

/// <summary>ReferTo - a REFER was received on a channel (Asterisk 22+).</summary>
public sealed class ReferToEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? ReferTo { get; set; }
    public string? ReferredBy { get; set; }
}

/// <summary>ReferredBy - channel was created by a REFER (Asterisk 22+).</summary>
public sealed class ReferredByEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
    public string? ReferredBy { get; set; }
}

/// <summary>RequiredDestination - channel requires an explicit destination (Asterisk 22+).</summary>
public sealed class RequiredDestinationEvent : AriEvent
{
    public AriChannel? Channel { get; set; }
}
