using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowEndpoint")]
public sealed class PJSipShowEndpointAction : ManagerAction, IEventGeneratingAction
{
    public string? Endpoint { get; set; }
}

