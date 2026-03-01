using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("SipShowPeer")]
public sealed class SipShowPeerResponse : ManagerResponse
{
    public string? ChannelType { get; set; }
    public string? ObjectName { get; set; }
    public string? ChanObjectType { get; set; }
    public bool? SecretExist { get; set; }
    public bool? Md5SecretExist { get; set; }
    public bool? RemoteSecretExist { get; set; }
    public string? Context { get; set; }
    public string? Language { get; set; }
    public string? AccountCode { get; set; }
    public string? AmaFlags { get; set; }
    public string? CidCallingPres { get; set; }
    public string? SipFromUser { get; set; }
    public string? SipFromDomain { get; set; }
    public string? CallGroup { get; set; }
    public string? PickupGroup { get; set; }
    public string? VoiceMailbox { get; set; }
    public string? TransferMode { get; set; }
    public int? LastMsgsSent { get; set; }
    public int? CallLimit { get; set; }
    public int? BusyLevel { get; set; }
    public int? MaxCallBr { get; set; }
    public bool? Dynamic { get; set; }
    public string? CallerId { get; set; }
    public long? RegExpire { get; set; }
    public bool? SipAuthInsecure { get; set; }
    public bool? SipNatSupport { get; set; }
    public bool? Acl { get; set; }
    public bool? SipT38support { get; set; }
    public string? SipT38ec { get; set; }
    public long? SipT38MaxDtgrm { get; set; }
    public bool? SipDirectMedia { get; set; }
    public bool? SipCanReinvite { get; set; }
    public bool? SipPromiscRedir { get; set; }
    public bool? SipUserPhone { get; set; }
    public bool? SipVideoSupport { get; set; }
    public bool? SipTextSupport { get; set; }
    public string? SipSessTimers { get; set; }
    public string? SipSessRefresh { get; set; }
    public int? SipSessExpires { get; set; }
    public int? SipSessMin { get; set; }
    public string? SipDtmfMode { get; set; }
    public string? ToHost { get; set; }
    public string? AddressIp { get; set; }
    public int? AddressPort { get; set; }
    public string? DefaultAddrIp { get; set; }
    public int? DefaultAddrPort { get; set; }
    public string? DefaultUsername { get; set; }
    public string? RegExtension { get; set; }
    public string? Codecs { get; set; }
    public string? CodecOrder { get; set; }
    public string? Status { get; set; }
    public string? SipUserAgent { get; set; }
    public string? ParkingLot { get; set; }
    public string? RegContact { get; set; }
    public int? QualifyFreq { get; set; }
    public int? MaxForwards { get; set; }
    public string? ToneZone { get; set; }
    public string? SipUseReasonHeader { get; set; }
    public string? SipEncryption { get; set; }
    public string? SipForcerport { get; set; }
    public string? SipRtpEngine { get; set; }
    public string? SipComedia { get; set; }
    public string? Mohsuggest { get; set; }
    public string? NamedPickupgroup { get; set; }
    public string? SipRtcpMux { get; set; }
    public string? Description { get; set; }
    public string? Subscribecontext { get; set; }
    public string? NamedCallgroup { get; set; }
}

