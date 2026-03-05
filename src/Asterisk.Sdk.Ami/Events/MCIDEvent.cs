using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MCID")]
public sealed class MCIDEvent : ChannelEventBase
{
    public string? MCallerIDNumValid { get; set; }
    public string? MCallerIDNum { get; set; }
    public string? MCallerIDton { get; set; }
    public string? MCallerIDNumPlan { get; set; }
    public string? MCallerIDNumPres { get; set; }
    public string? LinkedId { get; set; }
}
