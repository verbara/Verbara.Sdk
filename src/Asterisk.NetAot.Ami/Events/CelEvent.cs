using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Cel")]
public sealed class CelEvent : ManagerEvent
{
    public string? EventName { get; set; }
    public string? AccountCode { get; set; }
    public string? CallerIDani { get; set; }
    public string? CallerIDrdnis { get; set; }
    public string? CallerIDdnid { get; set; }
    public string? Application { get; set; }
    public string? AppData { get; set; }
    public string? EventTime { get; set; }
    public string? AmaFlags { get; set; }
    public string? UniqueID { get; set; }
    public string? LinkedID { get; set; }
    public string? UserField { get; set; }
    public string? Peer { get; set; }
    public string? PeerAccount { get; set; }
    public string? Extra { get; set; }
    public string? Channel { get; set; }
}

