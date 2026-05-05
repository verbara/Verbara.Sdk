using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("MixMonitor")]
public sealed class MixMonitorResponse : ManagerResponse
{
    public string? MixMonitorId { get; set; }
}

