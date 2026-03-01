using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ZapDNDOn")]
public sealed class ZapDndOnAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

