using Asterisk.Sdk.Sessions.Diagnostics;

namespace Asterisk.Sdk.Sessions.Manager;

internal sealed class SessionReconciler
{
    public static bool TryMarkOrphaned(CallSession session)
    {
        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Failed))
            {
                session.SetMetadata("cause", "orphaned");
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Failed, null, null, "orphaned"));
                SessionMetrics.SessionsOrphaned.Add(1);
                return true;
            }
        }
        return false;
    }

    public static bool TryMarkTimedOut(CallSession session)
    {
        lock (session.SyncRoot)
        {
            if (session.State is CallSessionState.Dialing or CallSessionState.Ringing)
            {
                if (session.TryTransition(CallSessionState.TimedOut))
                {
                    session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                        CallSessionEventType.TimedOut, null, null, "timeout"));
                    SessionMetrics.SessionsTimedOut.Add(1);
                    return true;
                }
            }
        }
        return false;
    }
}
