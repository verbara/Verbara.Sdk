using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeStopRecord")]
public sealed class ConfbridgeStopRecordAction : ManagerAction
{
    public string? Conference { get; set; }
}

