using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("Command")]
public sealed class CommandResponse : ManagerResponse
{
    public string? Privilege { get; set; }
}

