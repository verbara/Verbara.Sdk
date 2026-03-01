using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Hangup")]
public sealed class HangupAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public int? Cause { get; set; }
}

