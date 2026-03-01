using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Agents")]
public sealed class AgentsEvent : ResponseEvent
{
    public string? Agent { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? LoggedInChan { get; set; }
    public long? LoggedInTime { get; set; }
    public string? TalkingTo { get; set; }
    public string? TalkingToChan { get; set; }
}

