using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("EndpointDetail")]
public sealed class EndpointDetail : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
    public int? MaxVideoStreams { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public int? DeviceStateBusyAt { get; set; }
    public int? T38UdptlMaxdatagram { get; set; }
    public int? DtlsRekey { get; set; }
    public string? NamedPickupGroup { get; set; }
    public string? DirectMediaMethod { get; set; }
    public string? PickupGroup { get; set; }
    public string? SdpSession { get; set; }
    public string? DtlsVerify { get; set; }
    public string? MessageContext { get; set; }
    public string? Mailboxes { get; set; }
    public string? RecordOnFeature { get; set; }
    public string? DtlsPrivateKey { get; set; }
    public string? DtlsFingerprint { get; set; }
    public string? FromDomain { get; set; }
    public int? TimersSessExpires { get; set; }
    public string? NamedCallGroup { get; set; }
    public string? DtlsCipher { get; set; }
    public string? Aors { get; set; }
    public string? IdentifyBy { get; set; }
    public string? CalleridPrivacy { get; set; }
    public string? MwiSubscribeReplacesUnsolicited { get; set; }
    public string? Context { get; set; }
    public string? Transport { get; set; }
    public string? MohSuggest { get; set; }
    public bool? SrtpTag32 { get; set; }
    public int? MaxAudioStreams { get; set; }
    public string? CallGroup { get; set; }
    public int? FaxDetectTimeout { get; set; }
    public string? SdpOwner { get; set; }
    public string? CalleridTag { get; set; }
    public int? RtpTimeoutHold { get; set; }
    public string? MediaAddress { get; set; }
    public string? VoicemailExtension { get; set; }
    public int? RtpTimeout { get; set; }
    public string? SetVar { get; set; }
    public string? ContactAcl { get; set; }
    public string? RecordOffFeature { get; set; }
    public string? FromUser { get; set; }
    public string? ToneZone { get; set; }
    public string? Language { get; set; }
    public string? Callerid { get; set; }
    public string? CosAudio { get; set; }
    public string? CosVideo { get; set; }
    public string? DtlsAutoGenerateCert { get; set; }
    public string? MwiFromUser { get; set; }
    public string? Accountcode { get; set; }
    public string? Allow { get; set; }
    public string? RtpEngine { get; set; }
    public string? SubscribeContext { get; set; }
    public string? IncomingMwiMailbox { get; set; }
    public string? Auth { get; set; }
    public string? DirectMediaGlareMitigation { get; set; }
    public string? DtmfMode { get; set; }
    public string? OutboundAuth { get; set; }
    public string? TosVideo { get; set; }
    public string? TosAudio { get; set; }
    public string? DtlsCertFile { get; set; }
    public string? DtlsCaPath { get; set; }
    public string? DtlsSetup { get; set; }
    public string? ConnectedLineMethod { get; set; }
    [AsteriskMapping("100rel")]
    public string? Rel100 { get; set; }
    public string? Timers { get; set; }
    public string? Acl { get; set; }
    public int? TimersMinSe { get; set; }
    public int? SubMinExpiry { get; set; }
    public int? RtpKeepalive { get; set; }
    public string? T38UdptlEc { get; set; }
    public string? DtlsCaFile { get; set; }
    public string? OutboundProxy { get; set; }
    public string? DeviceState { get; set; }
    public string? ActiveChannels { get; set; }
    public bool? Ignore183withoutsdp { get; set; }
}

