using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeStopRecord")]
public sealed class ConfbridgeStopRecordAction : ManagerAction
{
    public string? Conference { get; set; }
}

