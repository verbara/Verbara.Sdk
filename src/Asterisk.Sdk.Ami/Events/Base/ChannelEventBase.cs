using Asterisk.Sdk;

namespace Asterisk.Sdk.Ami.Events.Base;

/// <summary>Base class for events related to a channel.</summary>
public class ChannelEventBase : ManagerEvent
{
    public string? Channel { get; set; }
    public string? ChannelState { get; set; }
    public string? ChannelStateDesc { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public string? ConnectedLineNum { get; set; }
    public string? ConnectedLineName { get; set; }
    public string? AccountCode { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
    public int? Priority { get; set; }
    public string? Language { get; set; }
    public string? Linkedid { get; set; }
}
