using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Reload")]
public sealed class ReloadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

