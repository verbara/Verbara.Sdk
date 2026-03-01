using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Park")]
public sealed class ParkAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Channel2 { get; set; }
    public int? Timeout { get; set; }
}

