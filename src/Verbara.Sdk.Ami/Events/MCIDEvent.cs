using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MCID")]
public sealed class MCIDEvent : ChannelEventBase
{
    public string? MCallerIDNumValid { get; set; }
    public string? MCallerIDNum { get; set; }
    public string? MCallerIDton { get; set; }
    public string? MCallerIDNumPlan { get; set; }
    public string? MCallerIDNumPres { get; set; }
    public string? LinkedId { get; set; }
}
