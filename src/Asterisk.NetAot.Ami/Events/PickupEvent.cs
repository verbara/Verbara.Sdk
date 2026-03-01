using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Pickup")]
public sealed class PickupEvent : ManagerEvent
{
    public string? Accountcode { get; set; }
    public string? Channel { get; set; }
    public string? Language { get; set; }
    public string? Linkedid { get; set; }
    public string? Targetaccountcode { get; set; }
    public string? Targetcalleridname { get; set; }
    public string? Targetcalleridnum { get; set; }
    public string? Targetchannel { get; set; }
    public string? Targetchannelstate { get; set; }
    public string? Targetchannelstatedesc { get; set; }
    public string? Targetconnectedlinename { get; set; }
    public string? Targetconnectedlinenum { get; set; }
    public string? Targetcontext { get; set; }
    public string? Targetexten { get; set; }
    public string? Targetlanguage { get; set; }
    public string? Targetlinkedid { get; set; }
    public string? Targetpriority { get; set; }
    public string? Targetuniqueid { get; set; }
    public string? Uniqueid { get; set; }
}

