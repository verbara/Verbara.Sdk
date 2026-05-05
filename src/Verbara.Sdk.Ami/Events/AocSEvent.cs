using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

/// <summary>Advice of Charge — Setup.</summary>
[VerbaraMapping("AOC-S")]
public sealed class AocSEvent : ChannelEventBase
{
    public string? Charge { get; set; }
    public string? LinkedId { get; set; }
}
