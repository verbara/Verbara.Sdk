using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "chan_sip was removed in Asterisk 21 (deprecated since 17); no supported Asterisk accepts SIPnotify. Use PJSIPNotifyAction. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("SipNotify")]
public sealed class SipNotifyAction : ManagerAction
{
    public string? Channel { get; set; }
}

