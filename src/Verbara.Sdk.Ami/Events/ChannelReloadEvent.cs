using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "chan_sip was removed in Asterisk 21 (deprecated since 17), and only chan_sip sent ChannelReload; no supported Asterisk sends it. No replacement. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("ChannelReload")]
public sealed class ChannelReloadEvent : ManagerEvent
{
    public string? ChannelType { get; set; }
    public string? Channel { get; set; }
    public int? PeerCount { get; set; }
    public int? RegistryCount { get; set; }
    public string? ReloadReason { get; set; }
    public string? ReloadReasonCode { get; set; }
    public string? ReloadReasonDescription { get; set; }
    public int? UserCount { get; set; }
}

