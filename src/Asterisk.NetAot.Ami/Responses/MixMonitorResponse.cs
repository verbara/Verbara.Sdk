using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("MixMonitor")]
public sealed class MixMonitorResponse : ManagerResponse
{
    public string? MixMonitorId { get; set; }
}

