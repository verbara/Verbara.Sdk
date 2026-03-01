using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ChanSpyStop")]
public sealed class ChanSpyStopEvent : ManagerEvent
{
    public string? SpyerChannel { get; set; }
    public int? SpyerChannelState { get; set; }
    public string? SpyerChannelStateDesc { get; set; }
    public string? SpyerCallerIdNum { get; set; }
    public string? SpyerCallerIdName { get; set; }
    public string? SpyerConnectedLineNum { get; set; }
    public string? SpyerConnectedLineName { get; set; }
    public string? SpyerLanguage { get; set; }
    public string? SpyerAccountCode { get; set; }
    public string? SpyerContext { get; set; }
    public string? SpyerExten { get; set; }
    public int? SpyerPriority { get; set; }
    public string? SpyerUniqueId { get; set; }
    public string? SpyerLinkedId { get; set; }
    public string? SpyeeChannel { get; set; }
    public int? SpyeeChannelState { get; set; }
    public string? SpyeeChannelStateDesc { get; set; }
    public string? SpyeeCallerIdNum { get; set; }
    public string? SpyeeCallerIdName { get; set; }
    public string? SpyeeConnectedLineNum { get; set; }
    public string? SpyeeConnectedLineName { get; set; }
    public string? SpyeeLanguage { get; set; }
    public string? SpyeeAccountCode { get; set; }
    public string? SpyeeContext { get; set; }
    public string? SpyeeExten { get; set; }
    public int? SpyeePriority { get; set; }
    public string? SpyeeUniqueId { get; set; }
    public string? SpyeeLinkedId { get; set; }
}

