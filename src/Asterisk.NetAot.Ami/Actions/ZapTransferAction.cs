using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ZapTransfer")]
public sealed class ZapTransferAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

