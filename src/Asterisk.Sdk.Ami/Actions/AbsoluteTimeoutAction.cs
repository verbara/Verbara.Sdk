using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("AbsoluteTimeout")]
public sealed class AbsoluteTimeoutAction : ManagerAction
{
    public string? Channel { get; set; }
    public int? Timeout { get; set; }
}

