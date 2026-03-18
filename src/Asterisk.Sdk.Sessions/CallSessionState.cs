namespace Asterisk.Sdk.Sessions;

public enum CallSessionState
{
    Created, Dialing, Ringing, Queued, Connected, OnHold,
    Transferring, Conference, Completed, Failed, TimedOut
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
