namespace Verbara.Sdk.Ami.Internal;

/// <summary>
/// Compatibility layer for MeetMe to ConfBridge migration.
/// Asterisk deprecated app_meetme in favor of ConfBridge in Asterisk 19, and no longer builds it by default.
/// This adapter translates ConfBridge events into MeetMe equivalents for
/// clients that still use the MeetMe event model.
/// </summary>
[Obsolete(
    "app_meetme is deprecated since Asterisk 19 and is not built by default. No replacement. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
public static class MeetmeCompatibility
{
    /// <summary>
    /// Check if a ConfBridge event should also generate a MeetMe equivalent.
    /// </summary>
    public static string? GetMeetMeEquivalent(string confBridgeEventType) =>
        confBridgeEventType switch
        {
            "ConfbridgeJoin" => "MeetMeJoin",
            "ConfbridgeLeave" => "MeetMeLeave",
            "ConfbridgeEnd" => "MeetMeEnd",
            "ConfbridgeTalking" => "MeetMeTalking",
            _ => null
        };
}
