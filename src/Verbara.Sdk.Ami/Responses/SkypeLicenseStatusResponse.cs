using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("SkypeLicenseStatus")]
public sealed class SkypeLicenseStatusResponse : ManagerResponse
{
    public int? CallsLicensed { get; set; }
}

