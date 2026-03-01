using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ZapHangup")]
public sealed class ZapHangupAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

