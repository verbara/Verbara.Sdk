using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleCEND")]
public sealed class DongleCENDEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Callidx { get; set; }
    public string? Cccause { get; set; }
    public string? Duration { get; set; }
    public string? Endstatus { get; set; }
}

