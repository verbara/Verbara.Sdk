using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("AGI")]
public sealed class AgiAction : ManagerAction, IEventGeneratingAction
{
    public string? Channel { get; set; }
    public string? Command { get; set; }
    public string? CommandId { get; set; }
}

