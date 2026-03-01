using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ZapDialOffhook")]
public sealed class ZapDialOffhookAction : ManagerAction
{
    public int? ZapChannel { get; set; }
    public string? Number { get; set; }
}

