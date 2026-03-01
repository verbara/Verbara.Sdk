using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Hangup")]
public sealed class HangupAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public int? Cause { get; set; }
}

