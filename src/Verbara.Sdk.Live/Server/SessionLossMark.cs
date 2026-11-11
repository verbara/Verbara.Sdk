namespace Verbara.Sdk.Live.Server;

/// <summary>
/// What the server's AMI connection may have lost before a position of its delivered event stream, for the session
/// engine: the connection instance's identity, the AMI session ordinal and how many events that session's buffer had
/// dropped, because it was full, before that position. Read only over the SDK's <c>AmiConnection</c>; over any other
/// connection there is no mark, never a default one. Two marks that differ mean the SDK may have missed an event between
/// the two positions; two that are equal mean it lost none it could know of. Compared for equality only.
/// </summary>
internal readonly record struct SessionLossMark(Guid ConnectionId, long Session, long DroppedBefore);
