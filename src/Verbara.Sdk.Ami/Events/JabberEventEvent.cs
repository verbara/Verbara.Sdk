using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 removed the JabberEvent event; no supported Asterisk sends it. No replacement. Removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("JabberEvent")]
public sealed class JabberEventEvent : ManagerEvent
{
    public string? Account { get; set; }
    public string? Packet { get; set; }
}

