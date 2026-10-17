using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 1.6 stopped sending AgentCallbackLogin, and chan_agent was removed in Asterisk 12; no supported Asterisk sends it. Use AgentLoginEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
}

