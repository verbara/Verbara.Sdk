using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "Asterisk 1.6 removed the Zaptel action ZapTransfer; chan_dahdi registers DAHDITransfer instead, and no supported Asterisk accepts ZapTransfer. Send the DAHDITransfer action (not typed by the SDK). This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("ZapTransfer")]
public sealed class ZapTransferAction : ManagerAction
{
    public int? ZapChannel { get; set; }
}

