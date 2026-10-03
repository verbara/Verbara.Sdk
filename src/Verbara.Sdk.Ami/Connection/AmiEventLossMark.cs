namespace Verbara.Sdk.Ami.Connection;

/// <summary>
/// What an <see cref="AmiConnection"/> may have lost before an event: the ordinal of the AMI session that delivered it
/// (one more at each session the connection starts), and how many events that session's buffer had dropped, because it
/// was full, when the event arrived. Two marks that differ mean a reconnect or a dropped event lies between the two
/// events. See <see cref="AmiConnection.EventLossMark"/>.
/// </summary>
internal readonly record struct AmiEventLossMark(long Session, long DroppedBefore);
