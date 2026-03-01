using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("PJSIPShowEndpoint")]
public sealed class PJSipShowEndpointAction : ManagerAction, IEventGeneratingAction
{
    public string? Endpoint { get; set; }
}

