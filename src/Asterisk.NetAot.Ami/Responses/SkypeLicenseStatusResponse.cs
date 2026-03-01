using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("SkypeLicenseStatus")]
public sealed class SkypeLicenseStatusResponse : ManagerResponse
{
    public int? CallsLicensed { get; set; }
}

