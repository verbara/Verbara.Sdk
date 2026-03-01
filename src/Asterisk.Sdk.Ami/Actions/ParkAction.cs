using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Park")]
public sealed class ParkAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Channel2 { get; set; }
    public int? Timeout { get; set; }
}

