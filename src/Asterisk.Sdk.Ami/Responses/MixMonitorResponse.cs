using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("MixMonitor")]
public sealed class MixMonitorResponse : ManagerResponse
{
    public string? MixMonitorId { get; set; }
}

