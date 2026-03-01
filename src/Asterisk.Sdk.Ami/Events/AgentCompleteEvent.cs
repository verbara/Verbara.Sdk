using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AgentComplete")]
public sealed class AgentCompleteEvent : AgentEventBase
{
    public long? HoldTime { get; set; }
    public long? TalkTime { get; set; }
    public string? Reason { get; set; }
    public string? DestExten { get; set; }
    public string? DestChannelStateDesc { get; set; }
    public string? DestUniqueId { get; set; }
    public string? DestConnectedLineNum { get; set; }
    public string? DestConnectedLineName { get; set; }
    public string? DestCallerIdName { get; set; }
    public string? DestCallerIdNum { get; set; }
    public string? DestContext { get; set; }
    public string? DestPriority { get; set; }
    public string? DestChannel { get; set; }
    public string? DestChannelState { get; set; }
    public string? Interface { get; set; }
    public string? Language { get; set; }
    public string? DestAccountCode { get; set; }
    public string? DestLanguage { get; set; }
    public string? LinkedId { get; set; }
    public string? DestLinkedId { get; set; }
    public string? AccountCode { get; set; }
}

