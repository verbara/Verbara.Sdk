using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeLicense")]
public sealed class SkypeLicenseEvent : ResponseEvent
{
    public string? Key { get; set; }
    public string? Expires { get; set; }
    public string? HostId { get; set; }
    public int? Channels { get; set; }
    public string? Status { get; set; }
}

