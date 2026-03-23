using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ShowDialplan")]
public sealed class ShowDialplanAction : ManagerAction, IEventGeneratingAction
{
    public string? Context { get; set; }
}

