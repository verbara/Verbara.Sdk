using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Reload")]
public sealed class ReloadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

