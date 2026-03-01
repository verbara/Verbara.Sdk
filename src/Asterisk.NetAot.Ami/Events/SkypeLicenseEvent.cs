using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SkypeLicense")]
public sealed class SkypeLicenseEvent : ResponseEvent
{
    public string? Key { get; set; }
    public string? Expires { get; set; }
    public string? HostId { get; set; }
    public int? Channels { get; set; }
    public string? Status { get; set; }
}

