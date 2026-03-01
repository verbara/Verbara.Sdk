using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Status")]
public sealed class StatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public string? Variables { get; set; }
}

