using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeStartRecord")]
public sealed class ConfbridgeStartRecordAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? RecordFile { get; set; }
}

