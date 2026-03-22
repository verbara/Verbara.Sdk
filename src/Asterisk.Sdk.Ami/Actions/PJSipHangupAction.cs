using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPHangup")]
public sealed class PJSipHangupAction : ManagerAction
{
    public string? Channel { get; set; }
    public int? Cause { get; set; }
}
