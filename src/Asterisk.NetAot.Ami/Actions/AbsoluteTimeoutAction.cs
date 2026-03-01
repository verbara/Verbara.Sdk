using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("AbsoluteTimeout")]
public sealed class AbsoluteTimeoutAction : ManagerAction
{
    public string? Channel { get; set; }
    public int? Timeout { get; set; }
}

