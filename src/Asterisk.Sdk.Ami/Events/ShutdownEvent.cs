using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Shutdown")]
public sealed class ShutdownEvent : ManagerEvent
{
    public string? Shutdown { get; set; }
    public bool? Restart { get; set; }
}

