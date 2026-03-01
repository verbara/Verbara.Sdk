using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeStartRecord")]
public sealed class ConfbridgeStartRecordAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? RecordFile { get; set; }
}

