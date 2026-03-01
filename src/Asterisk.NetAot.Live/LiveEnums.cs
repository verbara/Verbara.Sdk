namespace Asterisk.NetAot.Live;

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

/// <summary>MeetMe user state.</summary>
public enum MeetMeUserState { Joined, Left, Talking }
