namespace Verbara.Sdk.Live;

/// <summary>AMA (Automated Message Accounting) flags.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Standard Asterisk terminology")]
public enum AmaFlags { Unknown, Omit, Billing, Documentation, Default }

/// <summary>Call disposition for CDR.</summary>
public enum Disposition { Unknown, NoAnswer, Failed, Busy, Answered, Congestion }

/// <summary>Queue member state.</summary>
public enum QueueMemberState
{
    DeviceUnknown = 0,
    DeviceNotInUse = 1,
    DeviceInUse = 2,
    DeviceBusy = 3,
    DeviceInvalid = 4,
    DeviceUnavailable = 5,
    DeviceRinging = 6,
    DeviceRingInUse = 7,
    DeviceOnHold = 8
}

/// <summary>Queue entry (caller waiting) state.</summary>
public enum QueueEntryState { Joined, Abandoned, Completed }

/// <summary>Whether a conference participant is still in its room.</summary>
public enum ConferenceUserState
{
    /// <summary>The participant joined and is held in its room.</summary>
    Joined,

    /// <summary>The participant left, its room ended, or Live took it out of the table.</summary>
    Left,
}

/// <summary>The former name of <see cref="ConferenceUserState"/>.</summary>
/// <remarks><c>Talking</c> was never assigned: whether a participant is talking is <c>ConferenceUser.Talking</c>.</remarks>
[Obsolete(
    "Renamed to ConferenceUserState, which has Joined and Left with the same values. Talking was never assigned; read ConferenceUser.Talking. Removed in Verbara.Sdk 2.12.0.",
    DiagnosticId = "VSDK0008",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.11.0.md#vsdk0008")]
public enum MeetMeUserState { Joined, Left, Talking }
