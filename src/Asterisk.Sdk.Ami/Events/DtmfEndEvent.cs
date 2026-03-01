using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DtmfEnd")]
public sealed class DtmfEndEvent : ManagerEvent
{
    public int? DurationMs { get; set; }
}

