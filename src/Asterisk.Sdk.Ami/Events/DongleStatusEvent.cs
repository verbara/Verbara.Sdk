using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleStatus")]
public sealed class DongleStatusEvent : ManagerEvent
{
    public string? Device { get; set; }
    public string? Status { get; set; }
}

