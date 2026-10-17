using Verbara.Sdk;

namespace Verbara.Sdk.Ami.Events.Base;

/// <summary>Base class for MeetMe (legacy conference) events.</summary>
[Obsolete(
    "app_meetme is deprecated since Asterisk 19 and is not built by default. Use ConfbridgeEventBase. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
public class MeetMeEventBase : ManagerEvent
{
    public string? Meetme { get; set; }
    public string? Channel { get; set; }
    public int? Usernum { get; set; }
}
