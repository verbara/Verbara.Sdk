using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Unload")]
public sealed class UnloadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
}
