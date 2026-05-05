using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ZapDialOffhook")]
public sealed class ZapDialOffhookAction : ManagerAction
{
    public int? ZapChannel { get; set; }
    public string? Number { get; set; }
}

