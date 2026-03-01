using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("FaxLicenseStatus")]
public sealed class FaxLicenseStatusResponse : ManagerResponse
{
    public int? PortsLicensed { get; set; }
}

