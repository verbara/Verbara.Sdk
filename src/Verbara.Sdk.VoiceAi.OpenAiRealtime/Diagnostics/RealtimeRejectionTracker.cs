namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;

/// <summary>
/// What the vendor did with a bridge's last sessions, for <see cref="RealtimeHealthCheck"/>: how many consecutive
/// sessions it rejected, and the last rejection's code and time. One per bridge, written once per session from the
/// bridge's terminal block, after the session was classified.
/// </summary>
/// <remarks>
/// A rejection is a session the vendor refused at the upgrade with a 4xx status, or closed with a failure code before
/// any of its audio reached the caller. A served session ends the streak. A session the host cancelled, or one that
/// ended on a transport failure, is recorded as neither, so it leaves the streak as it was. No time window: the count
/// is of consecutive sessions only.
/// </remarks>
internal sealed class RealtimeRejectionTracker
{
    /// <summary>How many consecutive rejections turn the health entry <c>Degraded</c>.</summary>
    internal const int DegradedAfter = 3;

    private readonly Lock _gate = new();
    private int _consecutive;
    private int? _lastRejection;
    private DateTimeOffset? _lastRejectionAt;

    /// <summary>Records a rejected session: <paramref name="code"/> is the close code or the HTTP status.</summary>
    internal void Rejected(int code, DateTimeOffset at)
    {
        lock (_gate)
        {
            _consecutive++;
            _lastRejection = code;
            _lastRejectionAt = at;
        }
    }

    /// <summary>Records a session the vendor served: the streak of rejections ends.</summary>
    internal void Served()
    {
        lock (_gate)
            _consecutive = 0;
    }

    /// <summary>The streak and the last rejection, read together.</summary>
    internal (int ConsecutiveRejections, int? LastRejection, DateTimeOffset? LastRejectionAt) Read()
    {
        lock (_gate)
            return (_consecutive, _lastRejection, _lastRejectionAt);
    }
}
