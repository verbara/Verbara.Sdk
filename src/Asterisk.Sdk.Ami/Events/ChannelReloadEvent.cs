using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ChannelReload")]
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

