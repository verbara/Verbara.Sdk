using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "Asterisk 1.6 removed the AgentCallbackLogin action, and chan_agent was removed in Asterisk 12; no supported Asterisk accepts it. No replacement: use the AgentLogin dialplan application. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginAction : ManagerAction
{
    public string? Agent { get; set; }
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public bool? AckCall { get; set; }
    public long? WrapupTime { get; set; }
}

