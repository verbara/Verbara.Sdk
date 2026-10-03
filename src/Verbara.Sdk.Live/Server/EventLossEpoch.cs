namespace Verbara.Sdk.Live.Server;

/// <summary>
/// What the server may have lost before an event: how many reconnects it had reloaded after, and, over an
/// <c>AmiConnection</c>, the AMI session that delivered the event and how many events that session's buffer had
/// dropped, because it was full, when the event arrived. Two epochs that differ mean the SDK may have missed an event
/// between the two. Over any other <c>IAmiConnection</c> no dropped count is readable, so only reconnects move it.
/// </summary>
internal sealed record EventLossEpoch(long Reconnects, long AmiSession, long DroppedBefore);
