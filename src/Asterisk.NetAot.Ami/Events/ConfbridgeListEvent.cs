using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeList")]
public sealed class ConfbridgeListEvent : ResponseEvent
{
    public string? Conference { get; set; }
    public bool? Admin { get; set; }
    public bool? MarkedUser { get; set; }
    public string? Channel { get; set; }
    public string? Linkedid { get; set; }
    public string? Waiting { get; set; }
    public string? Language { get; set; }
    public string? Talking { get; set; }
    public string? Muted { get; set; }
    public string? Uniqueid { get; set; }
    public int? Answeredtime { get; set; }
    public string? Waitmarked { get; set; }
    public string? Endmarked { get; set; }
    public string? Accountcode { get; set; }
}

