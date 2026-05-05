using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ZapTransfer")]
public sealed class ZapTransferAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

