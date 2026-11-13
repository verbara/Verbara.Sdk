using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "No Asterisk registers an AMI action named exec; use CommandAction to run a CLI command, or RawAction to send an action by its name. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0007",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
[VerbaraMapping("exec")]
public sealed class ExecAction : ManagerAction
{
    public string? Command { get; set; }
}

