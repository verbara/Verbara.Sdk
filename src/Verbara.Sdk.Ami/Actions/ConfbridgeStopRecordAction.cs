using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeStopRecord")]
public sealed class ConfbridgeStopRecordAction : ManagerAction
{
    public string? Conference { get; set; }
}

