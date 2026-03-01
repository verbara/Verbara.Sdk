using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("GetVar")]
public sealed class GetVarResponse : ManagerResponse
{
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

