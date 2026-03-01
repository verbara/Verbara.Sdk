using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FullyBooted")]
public sealed class FullyBootedEvent : ManagerEvent
{
    public string? Status { get; set; }
    public string? Lastreload { get; set; }
    public int? Uptime { get; set; }
}

