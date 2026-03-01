using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ZapDialOffhook")]
public sealed class ZapDialOffhookAction : ManagerAction
{
    public int? ZapChannel { get; set; }
    public string? Number { get; set; }
}

