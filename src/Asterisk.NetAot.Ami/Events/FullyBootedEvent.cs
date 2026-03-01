using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("FullyBooted")]
public sealed class FullyBootedEvent : ManagerEvent
{
    public string? Status { get; set; }
    public string? Lastreload { get; set; }
    public int? Uptime { get; set; }
}

