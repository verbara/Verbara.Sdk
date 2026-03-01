using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AgentCalled")]
public sealed class AgentCalledEvent : ManagerEvent
{
    public string? Queue { get; set; }
    public string? AgentCalled { get; set; }
    public string? AgentName { get; set; }
    public string? ChannelCalling { get; set; }
    public string? DestinationChannel { get; set; }
    public string? CallerId { get; set; }
    public string? Extension { get; set; }
    public string? MemberName { get; set; }
    public string? DestExten { get; set; }
    public string? DestChannelStateDesc { get; set; }
    public string? DestUniqueId { get; set; }
    public string? DestConnectedLineNum { get; set; }
    public string? DestCallerIdName { get; set; }
    public string? DestCallerIdNum { get; set; }
    public string? DestContext { get; set; }
    public string? DestPriority { get; set; }
    public string? DestChannel { get; set; }
    public string? DestChannelState { get; set; }
    public string? Interface { get; set; }
    public string? Channel { get; set; }
    public string? DestConnectedLineName { get; set; }
    public string? DestAccountCode { get; set; }
    public string? Language { get; set; }
    public string? DestLanguage { get; set; }
    public string? LinkedId { get; set; }
    public string? DestLinkedId { get; set; }
    public string? Accountcode { get; set; }
}

