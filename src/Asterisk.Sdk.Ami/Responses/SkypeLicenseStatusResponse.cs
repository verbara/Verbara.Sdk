using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("SkypeLicenseStatus")]
public sealed class SkypeLicenseStatusResponse : ManagerResponse
{
    public int? CallsLicensed { get; set; }
}

