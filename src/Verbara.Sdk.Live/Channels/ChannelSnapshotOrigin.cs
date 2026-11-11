using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Channels;

/// <summary>
/// Where a reconciliation's snapshot was taken from, carried on every channel it admits: the server's loss mark sampled
/// when the read window opened, before <c>Status</c> was sent, so no later than any row the snapshot reports. One
/// instance per reconciliation, shared by its admissions; immutable.
/// </summary>
internal sealed class ChannelSnapshotOrigin(SessionLossMark mark)
{
    /// <summary>The loss mark sampled when the read window opened, before the snapshot was requested.</summary>
    public SessionLossMark Mark { get; } = mark;
}
