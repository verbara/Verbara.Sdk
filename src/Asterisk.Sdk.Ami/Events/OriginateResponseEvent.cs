using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("OriginateResponse")]
public sealed class OriginateResponseEvent : ResponseEvent
{
    public string? Response { get; set; }
    public string? Channel { get; set; }
    public int? Reason { get; set; }
    public string? Data { get; set; }
    public string? Application { get; set; }
}

