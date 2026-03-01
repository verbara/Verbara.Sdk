namespace Asterisk.Sdk.Enums;

/// <summary>
/// Asterisk hangup causes (AST_CAUSE_*), mapped from Q.931 cause codes.
/// </summary>
public enum HangupCause
{
    NotDefined = 0,
    UnallocatedNumber = 1,
    NoRouteTransitNet = 2,
    NoRouteDestination = 3,
    ChannelUnacceptable = 6,
    CallAwardedDelivered = 7,
    NormalClearing = 16,
    UserBusy = 17,
    NoUserResponse = 18,
    NoAnswer = 19,
    SubscriberAbsent = 20,
    CallRejected = 21,
    NumberChanged = 22,
    DestinationOutOfOrder = 27,
    InvalidNumberFormat = 28,
    FacilityRejected = 29,
    NormalUnspecified = 31,
    NormalCircuitCongestion = 34,
    NetworkOutOfOrder = 38,
    NormalTemporaryFailure = 41,
    SwitchCongestion = 42,
    AccessInfoDiscarded = 43,
    RequestedChannelNotAvailable = 44,
    FacilityNotSubscribed = 50,
    OutgoingCallBarred = 52,
    IncomingCallBarred = 54,
    BearerCapabilityNotAvailable = 58,
    BearerCapabilityNotImplemented = 65,
    ChannelNotImplemented = 66,
    FacilityNotImplemented = 69,
    InvalidCallReference = 81,
    IncompatibleDestination = 88,
    InvalidMessageUnspecified = 95,
    MandatoryInfoElementMissing = 96,
    MessageTypeNonExistent = 97,
    WrongMessage = 98,
    InfoElementNonExistent = 99,
    InvalidInfoElementContents = 100,
    MessageNotCompatibleWithCallState = 101,
    RecoveryOnTimerExpire = 102,
    Interworking = 127
}
