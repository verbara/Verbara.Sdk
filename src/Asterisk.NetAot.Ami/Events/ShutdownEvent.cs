using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Shutdown")]
public sealed class ShutdownEvent : ManagerEvent
{
    public string? Shutdown { get; set; }
    public bool? Restart { get; set; }
}

