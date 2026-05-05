using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DtmfEnd")]
public sealed class DtmfEndEvent : ManagerEvent
{
    public int? DurationMs { get; set; }
}

