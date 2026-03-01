using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("Ping")]
public sealed class PingResponse : ManagerResponse
{
    public string? Ping { get; set; }
    public string? Timestamp { get; set; }
}

