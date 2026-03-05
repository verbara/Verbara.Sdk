using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Load")]
public sealed class LoadEvent : ManagerEvent
{
    public string? Module { get; set; }
    public string? Status { get; set; }
}
