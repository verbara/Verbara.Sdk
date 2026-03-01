using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("Ping")]
public sealed class PingResponse : ManagerResponse
{
    public string? Ping { get; set; }
    public string? Timestamp { get; set; }
}

