using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeLicense")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeLicenseEvent : ResponseEvent
{
    public string? Key { get; set; }
    public string? Expires { get; set; }
    public string? HostId { get; set; }
    public int? Channels { get; set; }
    public string? Status { get; set; }
}

