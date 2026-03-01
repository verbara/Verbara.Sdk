using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Dial")]
public sealed class DialEvent : ManagerEvent
{
    public string? SubEvent { get; set; }
    public string? Channel { get; set; }
    public string? Src { get; set; }
    public string? Destination { get; set; }
    public string? DestChannel { get; set; }
    public string? CallerId { get; set; }
    public string? SrcUniqueId { get; set; }
    public string? DestUniqueId { get; set; }
    public string? DialString { get; set; }
    public string? DialStatus { get; set; }
    public int? DestChannelState { get; set; }
    public string? DestContext { get; set; }
    public int? DestPriority { get; set; }
    public string? DestChannelStateDesc { get; set; }
    public string? DestExten { get; set; }
    public string? DestConnectedLineName { get; set; }
    public string? DestConnectedLineNum { get; set; }
    public string? DestCallerIdName { get; set; }
    public string? DestCallerIdNum { get; set; }
}

