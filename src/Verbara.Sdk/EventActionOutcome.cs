// The namespace is the one the type had in Verbara.Sdk.Ami, which forwards it here: a package built against the type
// there binds to it by that full name.
namespace Verbara.Sdk.Ami.Connection;

/// <summary>
/// How an event-generating action ended, besides its events: written by the connection that ran the action, through
/// <see cref="IAmiConnection.SendEventGeneratingActionAsync(Verbara.Sdk.ManagerAction, EventActionOutcome, CancellationToken)"/>,
/// once the action's sequence has ended on its own.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk may refuse an action with <c>Response: Error</c>, or the AMI session may end before the action completes.
/// The sequence then ends as it does when Asterisk completes the action: with the events received so far, and no error.
/// The outcome tells those cases apart, but only when <see cref="Reported"/> is <see langword="true"/>: the connection
/// that ran the action said how it ended. With <see cref="Reported"/> <see langword="false"/> — a connection that cannot
/// say, a wrapper that does not forward the outcome, a mock, or a sequence that did not end on its own —
/// <see cref="Rejection"/> and <see cref="SessionEnded"/> carry no information.
/// </para>
/// <para>
/// Only the SDK's AMI connection writes an outcome; a caller creates an empty one, passes it in and reads it once the
/// sequence has ended. Read <see cref="IAmiConnection.ReportsEventActionOutcome"/> first to know whether the connection
/// can report one at all.
/// </para>
/// </remarks>
public sealed class EventActionOutcome
{
    /// <summary>An outcome that nothing has written yet, for the caller to pass in.</summary>
    public EventActionOutcome()
    {
        // Nothing to set: an action that has not ended is not reported, and was neither refused nor abandoned.
    }

    /// <summary>
    /// <see langword="true"/> when the connection that ran the action reported how it ended: its sequence ended on its
    /// own, and <see cref="Rejection"/> and <see cref="SessionEnded"/> were written. <see langword="false"/> by default,
    /// and whenever nothing reported the outcome; <see cref="Rejection"/> and <see cref="SessionEnded"/> are then
    /// meaningless.
    /// </summary>
    public bool Reported { get; internal set; }

    /// <summary>
    /// The <c>Message</c> of the <c>Response: Error</c> with which Asterisk refused the action, or
    /// <see cref="string.Empty"/> for a refusal without one; <see langword="null"/> when Asterisk did not refuse it.
    /// Meaningful only when <see cref="Reported"/> is <see langword="true"/>.
    /// </summary>
    public string? Rejection { get; internal set; }

    /// <summary>
    /// <see langword="true"/> when the connection gave the action up because its AMI session ended before Asterisk
    /// completed it; the events received until then were delivered. Meaningful only when <see cref="Reported"/> is
    /// <see langword="true"/>.
    /// </summary>
    public bool SessionEnded { get; internal set; }
}
