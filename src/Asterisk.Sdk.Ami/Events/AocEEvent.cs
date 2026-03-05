using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>Advice of Charge — End of call.</summary>
[AsteriskMapping("AOC-E")]
public sealed class AocEEvent : ChannelEventBase
{
    public string? Charge { get; set; }
    public string? Type { get; set; }
    public string? BillingID { get; set; }
    public string? TotalType { get; set; }
    public string? Currency { get; set; }
    public string? CurrencyName { get; set; }
    public string? CurrencyAmount { get; set; }
    public string? LinkedId { get; set; }
}
