using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 11 stopped sending AgentCallbackLogoff, and chan_agent was removed in Asterisk 12; no supported Asterisk sends it. Use AgentLogoffEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("AgentCallbackLogoff")]
public sealed class AgentCallbackLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
    public long? LoginTime { get; set; }
    public string? Reason { get; set; }
}

