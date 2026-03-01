using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("LocalBridge")]
public sealed class LocalBridgeEvent : ManagerEvent
{
    public string? UniqueId1 { get; set; }
    public string? UniqueId2 { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
    public string? CallerId1 { get; set; }
    public string? CallerId2 { get; set; }
    public string? LocalOptimization { get; set; }
    public string? LocalOneCalleridName { get; set; }
    public string? LocalTwoChannel { get; set; }
    public string? LocalTwoLanguage { get; set; }
    public string? LocalTwoExten { get; set; }
    public string? LocalOneChannel { get; set; }
    public string? LocalOneContext { get; set; }
    public string? LocalOneConnectedLineNum { get; set; }
    public string? LocalOneConnectedLineName { get; set; }
    public string? LocalOneChannelStateDesc { get; set; }
    public string? LocalOneChannelState { get; set; }
    public string? LocalOneExten { get; set; }
    public string? LocalOneLanguage { get; set; }
    public string? LocalOnePriority { get; set; }
    public string? LocalOneUniqueId { get; set; }
    public string? LocalTwochannelState { get; set; }
    public string? LocalTwoChannelStateDesc { get; set; }
    public string? LocalTwoPriority { get; set; }
    public string? LocalTwoContext { get; set; }
    public string? LocalTwoCalleridNum { get; set; }
    public string? LocalTwoCalleridName { get; set; }
    public string? LocalTwoUniqueid { get; set; }
    public string? LocalOneCalleridNum { get; set; }
    public string? LocalTwoConnectedLineName { get; set; }
    public string? LocalTwoConnectedLineNum { get; set; }
    public string? LocalTwoLinkedid { get; set; }
    public string? LocalOneLinkedid { get; set; }
    public string? LocalOneAccountCode { get; set; }
    public string? LocalTwoAccountCode { get; set; }
}

