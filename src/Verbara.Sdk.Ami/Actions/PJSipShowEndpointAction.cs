using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PJSIPShowEndpoint")]
public sealed class PJSipShowEndpointAction : ManagerAction, IEventGeneratingAction
{
    public string? Endpoint { get; set; }
}

