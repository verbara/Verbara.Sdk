using System.Text.Json.Serialization;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Events;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>
/// Source-generated JSON context for AOT-compatible serialization of ARI models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AriChannel))]
[JsonSerializable(typeof(AriChannel[]))]
[JsonSerializable(typeof(AriBridge))]
[JsonSerializable(typeof(AriBridge[]))]
[JsonSerializable(typeof(AriChannelState))]
[JsonSerializable(typeof(AriPlayback))]
[JsonSerializable(typeof(AriPlayback[]))]
[JsonSerializable(typeof(AriLiveRecording))]
[JsonSerializable(typeof(AriLiveRecording[]))]
[JsonSerializable(typeof(AriStoredRecording))]
[JsonSerializable(typeof(AriStoredRecording[]))]
[JsonSerializable(typeof(AriEndpoint))]
[JsonSerializable(typeof(AriEndpoint[]))]
[JsonSerializable(typeof(AriApplication))]
[JsonSerializable(typeof(AriApplication[]))]
[JsonSerializable(typeof(AriSound))]
[JsonSerializable(typeof(AriSound[]))]
[JsonSerializable(typeof(AriFormatLang))]
[JsonSerializable(typeof(AriFormatLang[]))]
[JsonSerializable(typeof(AriCallerId))]
[JsonSerializable(typeof(AriDialplanCep))]
[JsonSerializable(typeof(AriVariable))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Existing events
[JsonSerializable(typeof(StasisStartEvent))]
[JsonSerializable(typeof(StasisEndEvent))]
[JsonSerializable(typeof(ChannelStateChangeEvent))]
[JsonSerializable(typeof(ChannelDtmfReceivedEvent))]
[JsonSerializable(typeof(ChannelHangupRequestEvent))]
[JsonSerializable(typeof(BridgeCreatedEvent))]
[JsonSerializable(typeof(BridgeDestroyedEvent))]
[JsonSerializable(typeof(ChannelEnteredBridgeEvent))]
[JsonSerializable(typeof(ChannelLeftBridgeEvent))]
[JsonSerializable(typeof(PlaybackStartedEvent))]
[JsonSerializable(typeof(PlaybackFinishedEvent))]
[JsonSerializable(typeof(DialEvent))]
[JsonSerializable(typeof(ChannelToneDetectedEvent))]
// New events
[JsonSerializable(typeof(ChannelCreatedEvent))]
[JsonSerializable(typeof(ChannelDestroyedEvent))]
[JsonSerializable(typeof(ChannelVarsetEvent))]
[JsonSerializable(typeof(ChannelHoldEvent))]
[JsonSerializable(typeof(ChannelUnholdEvent))]
[JsonSerializable(typeof(ChannelTalkingStartedEvent))]
[JsonSerializable(typeof(ChannelTalkingFinishedEvent))]
[JsonSerializable(typeof(ChannelConnectedLineEvent))]
[JsonSerializable(typeof(RecordingStartedEvent))]
[JsonSerializable(typeof(RecordingFinishedEvent))]
[JsonSerializable(typeof(EndpointStateChangeEvent))]
// Sprint 1 — Transfer and recording events
[JsonSerializable(typeof(BridgeAttendedTransferEvent))]
[JsonSerializable(typeof(BridgeBlindTransferEvent))]
[JsonSerializable(typeof(ChannelTransferEvent))]
[JsonSerializable(typeof(BridgeMergedEvent))]
[JsonSerializable(typeof(BridgeVideoSourceChangedEvent))]
[JsonSerializable(typeof(RecordingFailedEvent))]
// Sprint 3 — Complementary ARI events
[JsonSerializable(typeof(ChannelCallerIdEvent))]
[JsonSerializable(typeof(ChannelDialplanEvent))]
[JsonSerializable(typeof(ChannelUsereventEvent))]
[JsonSerializable(typeof(DeviceStateChangedEvent))]
[JsonSerializable(typeof(PlaybackContinuingEvent))]
[JsonSerializable(typeof(ContactStatusChangeEvent))]
[JsonSerializable(typeof(PeerStatusChangeEvent))]
[JsonSerializable(typeof(TextMessageReceivedEvent))]
// Auxiliary models
[JsonSerializable(typeof(AriDeviceState))]
[JsonSerializable(typeof(AriContactInfo))]
[JsonSerializable(typeof(AriPeer))]
[JsonSerializable(typeof(AriTextMessage))]
// Sprint 5 — ARI events for Asterisk 16-22+
[JsonSerializable(typeof(ApplicationMoveFailedEvent))]
[JsonSerializable(typeof(ApplicationRegisteredEvent))]
[JsonSerializable(typeof(ApplicationUnregisteredEvent))]
[JsonSerializable(typeof(MissingParamsEvent))]
[JsonSerializable(typeof(ReferToEvent))]
[JsonSerializable(typeof(ReferredByEvent))]
[JsonSerializable(typeof(RequiredDestinationEvent))]
public sealed partial class AriJsonContext : JsonSerializerContext;
