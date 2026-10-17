using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 removed the ChannelUpdate event; no supported Asterisk sends it. No replacement. Removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("ChannelUpdate")]
public sealed class ChannelUpdateEvent : ManagerEvent
{
    public string? ChannelType { get; set; }
    public string? Channel { get; set; }
    public string? SipCallId { get; set; }
    public string? SipFullContact { get; set; }
    public string? PeerName { get; set; }
    public string? GtalkSid { get; set; }
    public string? Iax2CallNoLocal { get; set; }
    public string? Iax2CallNoRemote { get; set; }
    public string? Iax2Peer { get; set; }
}

