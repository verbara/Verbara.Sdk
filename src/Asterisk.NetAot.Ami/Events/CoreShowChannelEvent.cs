using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("CoreShowChannel")]
public sealed class CoreShowChannelEvent : ResponseEvent
{
    public string? Accountcode { get; set; }
    public string? Application { get; set; }
    public string? Applicationdata { get; set; }
    public string? BridgedChannel { get; set; }
    public string? Bridgeduniqueid { get; set; }
    public string? Bridgeid { get; set; }
    public string? Channel { get; set; }
    public string? Duration { get; set; }
    public string? Extension { get; set; }
    public string? Uniqueid { get; set; }
    public string? Linkedid { get; set; }
    public string? Language { get; set; }
}

