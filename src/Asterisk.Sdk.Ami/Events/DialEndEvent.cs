using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DialEnd")]
public sealed class DialEndEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? ChannelState { get; set; }
    public string? ChannelStateDesc { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public string? ConnectedLineNum { get; set; }
    public string? ConnectedLineName { get; set; }
    public string? Language { get; set; }
    public string? AccountCode { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
    public int? Priority { get; set; }
    public string? Linkedid { get; set; }
    public string? DestChannel { get; set; }
    public string? DestChannelState { get; set; }
    public string? DestChannelStateDesc { get; set; }
    public string? DestCallerIdNum { get; set; }
    public string? DestCallerIdName { get; set; }
    public string? DestConnectedLineNum { get; set; }
    public string? DestConnectedLineName { get; set; }
    public string? DestLanguage { get; set; }
    public string? DestAccountCode { get; set; }
    public string? DestContext { get; set; }
    public string? DestExten { get; set; }
    public int? DestPriority { get; set; }
    public string? DestUniqueid { get; set; }
    public string? DestLinkedid { get; set; }
    public string? DialStatus { get; set; }
    public string? Forward { get; set; }
}

