using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleNewCMGR")]
public sealed class DongleNewCMGREvent : ManagerEvent
{
    public string? Device { get; set; }
}

