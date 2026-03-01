using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("GetVar")]
public sealed class GetVarResponse : ManagerResponse
{
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

