using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>Advice of Charge — Setup.</summary>
[AsteriskMapping("AOC-S")]
public sealed class AocSEvent : ChannelEventBase
{
    public string? Charge { get; set; }
    public string? LinkedId { get; set; }
}
