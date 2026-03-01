using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleDeviceEntry")]
public sealed class DongleDeviceEntryEvent : ResponseEvent
{
    public string? Device { get; set; }
    public string? AudioSetting { get; set; }
    public string? DataSetting { get; set; }
    public string? IMEISetting { get; set; }
    public string? IMSISetting { get; set; }
    public string? ChannelLanguage { get; set; }
    public string? Group { get; set; }
    public string? RXGain { get; set; }
    public string? TXGain { get; set; }
    public string? U2DIAG { get; set; }
    public string? UseCallingPres { get; set; }
    public string? DefaultCallingPres { get; set; }
    public string? AutoDeleteSMS { get; set; }
    public string? DisableSMS { get; set; }
    public string? ResetDongle { get; set; }
    public string? SMSPDU { get; set; }
    public string? CallWaitingSetting { get; set; }
    public string? DTMF { get; set; }
    public string? MinimalDTMFGap { get; set; }
    public string? MinimalDTMFDuration { get; set; }
    public string? MinimalDTMFInterval { get; set; }
    public string? State { get; set; }
    public string? AudioState { get; set; }
    public string? DataState { get; set; }
    public string? Voice { get; set; }
    public string? SMS { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? IMEIState { get; set; }
    public string? GSMRegistrationStatus { get; set; }
    public string? RSSI { get; set; }
    public string? Mode { get; set; }
    public string? Submode { get; set; }
    public string? ProviderName { get; set; }
    public string? LocationAreaCode { get; set; }
    public string? CellID { get; set; }
    public string? SubscriberNumber { get; set; }
    public string? SMSServiceCenter { get; set; }
    public string? UseUCS2Encoding { get; set; }
    public string? USSDUse7BitEncoding { get; set; }
    public string? USSDUseUCS2Decoding { get; set; }
    public string? TasksInQueue { get; set; }
    public string? CommandsInQueue { get; set; }
    public string? CallWaitingState { get; set; }
    public string? CurrentDeviceState { get; set; }
    public string? DesiredDeviceState { get; set; }
    public string? CallsChannels { get; set; }
    public string? Active { get; set; }
    public string? Held { get; set; }
    public string? Dialing { get; set; }
    public string? Alerting { get; set; }
    public string? Incoming { get; set; }
    public string? Waiting { get; set; }
    public string? Releasing { get; set; }
    public string? Initializing { get; set; }
}

