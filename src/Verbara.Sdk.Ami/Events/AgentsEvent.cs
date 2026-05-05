using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Agents")]
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

