using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("FaxLicenseStatus")]
public sealed class FaxLicenseStatusResponse : ManagerResponse
{
    public int? PortsLicensed { get; set; }
}

