using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PJSIPHangup")]
public sealed class PJSipHangupAction : ManagerAction
{
    public string? Channel { get; set; }
    public int? Cause { get; set; }
}
