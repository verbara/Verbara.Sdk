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
