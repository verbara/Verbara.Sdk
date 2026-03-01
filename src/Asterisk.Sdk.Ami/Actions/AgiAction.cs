using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("AGI")]
public sealed class AgiAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public string? Command { get; set; }
    public string? CommandId { get; set; }
}

