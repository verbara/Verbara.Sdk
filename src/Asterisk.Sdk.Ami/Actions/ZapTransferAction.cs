using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ZapTransfer")]
public sealed class ZapTransferAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

