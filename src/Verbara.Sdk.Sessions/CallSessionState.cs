namespace Verbara.Sdk.Sessions;

public enum CallSessionState
{
    Created, Dialing, Ringing, Queued, Connected, OnHold,
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
        [CallSessionState.Connected] = [CallSessionState.OnHold, CallSessionState.Transferring, CallSessionState.Conference, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.OnHold] = [CallSessionState.Connected, CallSessionState.Transferring, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.Transferring] = [CallSessionState.Connected, CallSessionState.Failed],
        [CallSessionState.Conference] = [CallSessionState.Connected, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.Completed] = [],
        [CallSessionState.Failed] = [],
        [CallSessionState.TimedOut] = [],
    };

    public static bool IsValid(CallSessionState from, CallSessionState to) =>
        ValidTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
}
