namespace Verbara.Sdk.Live.Conferences;

/// <summary>A participant of a <see cref="ConferenceRoom"/>.</summary>
/// <remarks>
/// Only the manager creates and changes participants. Until 2.12.0 every participant it reports is also a
/// <c>MeetMeUser</c>, the same object under its former name.
/// </remarks>
public class ConferenceUser
{
    internal ConferenceUser()
    {
    }

    /// <summary>
    /// The user number exactly as Asterisk sent it. It is <c>0</c> for every ConfBridge participant,
    /// because ConfBridge sends none; it never identifies a participant (the key is <see cref="Channel"/>).
    /// </summary>
    public int UserNum { get; internal init; }

    /// <summary>The channel name Asterisk sent on the participant's join; the participant's key in its room.</summary>
    public string Channel { get; internal init; } = string.Empty;

    /// <summary>
    /// The <c>Uniqueid</c> of the participant's join as Asterisk sent it, or <see langword="null"/>
    /// when the join carried none or was recorded without it. It decides no replacement and no
    /// removal: the key is <see cref="Channel"/>, and a leave is matched by it. It tells one thing
    /// apart: a join for a held channel with another <c>Uniqueid</c> leaves the replaced participant
    /// reading <see cref="ConferenceUserState.Left"/> and <see cref="LeaveNotObserved"/>
    /// <see langword="true"/>, where the same <c>Uniqueid</c>, or a side without one, leaves it as it was.
    /// </summary>
    public string? UniqueId { get; internal init; }

    /// <summary>Whether the participant is still in its room: <see cref="ConferenceUserState.Joined"/> until it leaves.</summary>
    public ConferenceUserState State { get; internal set; }

    /// <summary>
    /// Whether the participant is muted, as Asterisk last reported it: set from the join's
    /// <c>Muted</c> header (an absent or unreadable value reads <see langword="false"/>), then by
    /// each <c>ConfbridgeMute</c> (<see langword="true"/>) and <c>ConfbridgeUnmute</c>
    /// (<see langword="false"/>) that names this participant.
    /// </summary>
    /// <remarks>
    /// This is the participant's mute request as Asterisk reports it, not whether the participant is
    /// audible: a participant on hold music, or a wait-marked participant with no marked user present,
    /// is silent while reading <see langword="false"/>. A whole-room mute from the conference keypad
    /// sends no event, so the participants keep reading <see langword="false"/> until a later
    /// per-participant event or a reload of the conference table reads the real state.
    /// </remarks>
    public bool Muted { get; internal set; }

    /// <summary>
    /// Whether the participant is talking, as the last <c>ConfbridgeTalking</c> naming it reported
    /// (<c>TalkingStatus</c>; an absent or unreadable status leaves the value unchanged).
    /// <see langword="false"/> from the join.
    /// </summary>
    /// <remarks>
    /// Asterisk sends <c>ConfbridgeTalking</c> only for a ConfBridge user profile with
    /// <c>talk_detection_events=yes</c> (the default is <c>no</c>); without it this stays
    /// <see langword="false"/>.
    /// </remarks>
    public bool Talking { get; internal set; }

    /// <summary>
    /// What Live observed of this participant's leave, which is not a statement of what Asterisk did:
    /// <see langword="true"/> exactly when Live took the participant out of the table without having observed a report
    /// of a leave for its channel name, or of the end of its room, before the removal; <see langword="false"/> otherwise.
    /// Set before <see cref="ConferenceManager.UserLeft"/> is raised and never cleared, so a handler reads the final value.
    /// These paths set it:
    /// <list type="bullet">
    /// <item>a reload's completed conference listing listed its room and the room's participant listing omitted it;</item>
    /// <item>that listing did not list its room, including the answer <c>No active conferences.</c>;</item>
    /// <item>a join with another <c>Uniqueid</c> replaced it under its channel name, which raises no
    /// <see cref="ConferenceManager.UserLeft"/>;</item>
    /// <item><see cref="ConferenceManager.Clear"/> emptied the table, which raises none either.</item>
    /// </list>
    /// <para>
    /// Leaves are matched by channel name, so <see langword="true"/> does not prove Asterisk sent no leave: a report Live
    /// could not match to a held participant (a renamed channel, a reused name) is not observed, and the next
    /// reconciliation reads <see langword="true"/>. A report that names a reused channel is applied to the participant
    /// held under that name, which reads <see langword="false"/> although it did not leave.
    /// </para>
    /// <para>
    /// A leave with this set has no Asterisk timestamp or reason.
    /// </para>
    /// </summary>
    public bool LeaveNotObserved { get; private set; }

    internal void MarkLeaveNotObserved() => LeaveNotObserved = true;

    // When the manager took this participant in, on its change ordinal: a listing requested before it cannot report it.
    internal long JoinMark { get; init; }
}
