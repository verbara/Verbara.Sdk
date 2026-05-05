using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Dtmf")]
public sealed class DtmfEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? Digit { get; set; }
    public string? Direction { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? AccountCode { get; set; }
}

