using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AlarmClear")]
public sealed class AlarmClearEvent : ManagerEvent
{
    public int? Channel { get; set; }
}

