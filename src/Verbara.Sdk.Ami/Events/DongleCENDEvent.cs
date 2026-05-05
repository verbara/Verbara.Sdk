using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DongleCEND")]
public sealed class DongleCENDEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Callidx { get; set; }
    public string? Cccause { get; set; }
    public string? Duration { get; set; }
    public string? Endstatus { get; set; }
}

