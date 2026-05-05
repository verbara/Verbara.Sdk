using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("GetVar")]
public sealed class GetVarResponse : ManagerResponse
{
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

