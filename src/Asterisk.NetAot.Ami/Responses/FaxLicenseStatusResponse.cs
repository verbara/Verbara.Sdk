using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("FaxLicenseStatus")]
public sealed class FaxLicenseStatusResponse : ManagerResponse
{
    public int? PortsLicensed { get; set; }
}

