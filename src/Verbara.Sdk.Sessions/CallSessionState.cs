namespace Verbara.Sdk.Sessions;

/// <summary>
/// Where a call stands now. The numeric value of every state is kept from release to release.
/// </summary>
public enum CallSessionState
{
    Created, Dialing, Ringing,

    /// <summary>
    /// Waiting in a queue until app_queue reports a connection or the call ends: a call that joins a queue waits here
    /// whether it joins from its initial, dialing or ringing state, or after it was connected or transferring. A call
    /// that ends here before any member answers ends <see cref="Failed"/> when it was never connected, a call the dialplan
    /// answered included; one that was connected before (a reload or an admission that opened it connected included)
    /// ends <see cref="Completed"/> when its caller's own channel hangs up with normal clearing or a reload proves it
    /// gone, and <see cref="Failed"/> on any other cause of that channel.
    /// </summary>
    Queued,

    /// <summary>
    /// In conversation with someone now. Each entry into this state from <see cref="Dialing"/>, <see cref="Ringing"/>,
    /// <see cref="Queued"/> or <see cref="Transferring"/> is announced by one <see cref="CallConnectedEvent"/>; a return
    /// from <see cref="OnHold"/> is not. A call a state reload or the admission of existing calls opens here was found
    /// connected, and its <see cref="CallSession.ConnectedAt"/> stays <c>null</c> until a connection of its own is observed.
    /// </summary>
    Connected,

    OnHold,
    Transferring, Conference, Completed, Failed,

    /// <summary>
    /// Not produced: the SDK ends no call in this state since 2.7.0. A call whose channels Asterisk no longer
    /// reports ends <see cref="Completed"/> or <see cref="Failed"/>.
    /// <para>
    /// Kept so that stored rows written by earlier versions keep binding; the stores and the session manager
    /// still read it as an ended state.
    /// </para>
    /// </summary>
    TimedOut
}

internal static class CallSessionStateTransitions
{
    private static readonly Dictionary<CallSessionState, HashSet<CallSessionState>> ValidTransitions = new()
    {
        [CallSessionState.Created] = [CallSessionState.Dialing, CallSessionState.Queued, CallSessionState.Failed],
        [CallSessionState.Dialing] = [CallSessionState.Ringing, CallSessionState.Queued, CallSessionState.Connected, CallSessionState.Failed, CallSessionState.TimedOut],
        [CallSessionState.Ringing] = [CallSessionState.Queued, CallSessionState.Connected, CallSessionState.Failed, CallSessionState.TimedOut],
        [CallSessionState.Queued] = [CallSessionState.Connected, CallSessionState.Failed, CallSessionState.TimedOut],
        [CallSessionState.Connected] = [CallSessionState.Queued, CallSessionState.OnHold, CallSessionState.Transferring, CallSessionState.Conference, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.OnHold] = [CallSessionState.Connected, CallSessionState.Transferring, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.Transferring] = [CallSessionState.Queued, CallSessionState.Connected, CallSessionState.Failed],
        [CallSessionState.Conference] = [CallSessionState.Connected, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.Completed] = [],
        [CallSessionState.Failed] = [],
        [CallSessionState.TimedOut] = [],
    };

    public static bool IsValid(CallSessionState from, CallSessionState to) =>
        ValidTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
}
