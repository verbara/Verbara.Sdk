using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SkypeAccountStatus")]
public sealed class SkypeAccountStatusEvent : ManagerEvent
{
    public string? Username { get; set; }
    public string? Status { get; set; }
}

