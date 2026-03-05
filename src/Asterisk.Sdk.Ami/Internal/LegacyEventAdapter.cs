#pragma warning disable CS0618 // Obsolete members — this adapter intentionally creates legacy events
using Asterisk.Sdk.Ami.Events;

namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Backwards compatibility shims for AMI events.
/// Synthesizes legacy events from modern ones for older client code:
/// DialBeginEvent to DialEvent, BridgeEnterEvent to LinkEvent,
/// BridgeLeaveEvent to UnlinkEvent (Asterisk pre-12 compatibility).
/// </summary>
public static class LegacyEventAdapter
{
    /// <summary>
    /// Given a modern event, return a legacy equivalent if applicable.
    /// Returns null if no legacy translation is needed.
    /// </summary>
    public static Asterisk.Sdk.ManagerEvent? CreateLegacyEvent(AmiMessage message)
    {
        var eventType = message.EventType;

        if (string.Equals(eventType, "DialBegin", StringComparison.OrdinalIgnoreCase))
        {
            return new DialEvent
            {
                EventType = "Dial",
                UniqueId = message["Uniqueid"],
                Privilege = message["Privilege"]
            };
        }

        if (string.Equals(eventType, "BridgeEnter", StringComparison.OrdinalIgnoreCase))
        {
            return new LinkEvent
            {
                EventType = "Link",
                UniqueId = message["Uniqueid"],
                Privilege = message["Privilege"]
            };
        }

        if (string.Equals(eventType, "BridgeLeave", StringComparison.OrdinalIgnoreCase))
        {
            return new UnlinkEvent
            {
                EventType = "Unlink",
                UniqueId = message["Uniqueid"],
                Privilege = message["Privilege"]
            };
        }

        return null;
    }
}
