using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("FaxLicense")]
public sealed class FaxLicenseEvent : ResponseEvent
{
    public string? File { get; set; }
    public string? HostId { get; set; }
    public string? Key { get; set; }
    public int? Ports { get; set; }
    public string? Product { get; set; }
    public string? Status { get; set; }
}

