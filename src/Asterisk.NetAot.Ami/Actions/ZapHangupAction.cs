using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ZapHangup")]
public sealed class ZapHangupAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

