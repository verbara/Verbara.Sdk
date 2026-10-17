using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "Asterisk 1.6 removed the Zaptel action ZapRestart; chan_dahdi registers DAHDIRestart instead, and no supported Asterisk accepts ZapRestart. Send the DAHDIRestart action (not typed by the SDK). This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("ZapRestart")]
public sealed class ZapRestartAction : ManagerAction
{
}

