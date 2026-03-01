using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("RequestBadFormat")]
public sealed class RequestBadFormatEvent : ManagerEvent
{
    public string? Severity { get; set; }
    public int? Eventversion { get; set; }
    public string? Sessiontv { get; set; }
    public string? Eventtv { get; set; }
    public string? Sessionid { get; set; }
    public string? Localaddress { get; set; }
    public string? Accountid { get; set; }
    public string? Requesttype { get; set; }
    public string? Service { get; set; }
    public string? Remoteaddress { get; set; }
    public string? Module { get; set; }
    public string? Requestparams { get; set; }
}

