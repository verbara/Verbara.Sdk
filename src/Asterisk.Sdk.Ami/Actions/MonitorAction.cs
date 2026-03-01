using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Monitor")]
public sealed class MonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
    public string? Format { get; set; }
    public bool? Mix { get; set; }
}

