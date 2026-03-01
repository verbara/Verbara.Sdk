using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeStartRecord")]
public sealed class ConfbridgeStartRecordAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? RecordFile { get; set; }
}

