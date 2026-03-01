using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DtmfEnd")]
public sealed class DtmfEndEvent : ManagerEvent
{
    public int? DurationMs { get; set; }
}

