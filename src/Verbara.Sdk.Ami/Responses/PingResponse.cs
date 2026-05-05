using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("Ping")]
public sealed class PingResponse : ManagerResponse
{
    public string? Ping { get; set; }
    public string? Timestamp { get; set; }
}

