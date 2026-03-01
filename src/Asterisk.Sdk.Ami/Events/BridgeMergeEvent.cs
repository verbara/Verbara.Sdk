using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeMerge")]
public sealed class BridgeMergeEvent : ManagerEvent
{
    public int? FromBridgeNumChannels { get; set; }
    public int? ToBridgeNumChannels { get; set; }
    public string? FromBridgeName { get; set; }
    public string? FromBridgeUniqueId { get; set; }
    public string? FromBridgeCreator { get; set; }
    public string? ToBridgeName { get; set; }
    public string? FromBridgeTechnology { get; set; }
    public string? ToBridgeUniqueId { get; set; }
    public string? ToBridgeTechnology { get; set; }
    public string? FromBridgeType { get; set; }
    public string? ToBridgeType { get; set; }
    public string? ToBridgeCreator { get; set; }
}

