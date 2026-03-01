namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Compatibility layer for MeetMe to ConfBridge migration.
/// Asterisk deprecated MeetMe in favor of ConfBridge starting in Asterisk 12.
/// This adapter translates ConfBridge events into MeetMe equivalents for
/// clients that still use the MeetMe event model.
/// </summary>
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
