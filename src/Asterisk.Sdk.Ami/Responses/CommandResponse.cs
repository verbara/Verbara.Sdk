using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("Command")]
public sealed class CommandResponse : ManagerResponse
{
    public string? Privilege { get; set; }
}

