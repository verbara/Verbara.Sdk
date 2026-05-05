using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("FaxLicense")]
public sealed class FaxLicenseEvent : ResponseEvent
{
    public string? File { get; set; }
    public string? HostId { get; set; }
    public string? Key { get; set; }
    public int? Ports { get; set; }
    public string? Product { get; set; }
    public string? Status { get; set; }
}

