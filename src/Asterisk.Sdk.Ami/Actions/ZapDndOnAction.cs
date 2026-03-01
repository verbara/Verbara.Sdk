using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ZapDNDOn")]
public sealed class ZapDndOnAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

