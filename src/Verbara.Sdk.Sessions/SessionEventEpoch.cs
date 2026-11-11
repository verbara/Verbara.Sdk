namespace Verbara.Sdk.Sessions;

/// <summary>
/// What the SDK may have lost of a server's events before the point a session notification reflects: the loss mark the
/// session engine stamps on <see cref="SessionDomainEvent.Epoch"/>. Compare two marks of notifications of the same
/// server with <see cref="LossBetween"/>, for equality only: equal marks mean the SDK missed no event of that server it
/// could know of between the two wire positions; any difference means it may have — a reconnect, an event the
/// connection dropped because its buffer was full, a detach of the server, or another connection instance. The fields
/// have no order: compare them for equality, never as magnitudes.
/// </summary>
/// <remarks>
/// <para>
/// The engine never stamps a <see langword="default"/> mark: when it cannot read one, the notification's
/// <see cref="SessionDomainEvent.Epoch"/> is <see langword="null"/>, which means cannot tell, never complete.
/// </para>
/// <para>
/// The field set is closed until the next major version: a positional record struct cannot gain a field without breaking
/// its constructor and <c>Deconstruct</c>.
/// </para>
/// </remarks>
/// <param name="ConnectionId">
/// The AMI connection instance that delivered the server's events, drawn once when it was built and unique across
/// processes, so a new instance, a failover or a restart never equals an earlier one.
/// </param>
/// <param name="AmiSession">
/// That connection's AMI session ordinal: one more at every session it starts, an automatic reconnect or the caller's
/// own connect.
/// </param>
/// <param name="DroppedBefore">
/// How many events that session had dropped, because its buffer was full, before the point the notification reflects.
/// </param>
/// <param name="Attachment">
/// The generation of the session engine's attachment of the server: one more at every real attach; unchanged by a detach
/// and by attaching the same instance again while it is attached.
/// </param>
public readonly record struct SessionEventEpoch(Guid ConnectionId, long AmiSession, long DroppedBefore, long Attachment)
{
    /// <summary>
    /// Whether the SDK may have lost an event of the server between two notifications of it, from their marks:
    /// <see cref="SessionEventLoss.Unknown"/> when either mark is <see langword="null"/>,
    /// <see cref="SessionEventLoss.NoneKnown"/> when both are equal, <see cref="SessionEventLoss.Possible"/> otherwise.
    /// </summary>
    /// <remarks>
    /// Use this rather than <c>==</c> on two nullable marks: lifted equality reads two absent marks as equal. The marks are
    /// wire positions, not times: a loss after the later notification's position shows on the next notification that
    /// reflects a later one.
    /// </remarks>
    /// <param name="earlier">The mark of the earlier notification.</param>
    /// <param name="later">The mark of the later notification.</param>
    /// <returns>The verdict for the interval between the two notifications.</returns>
    public static SessionEventLoss LossBetween(SessionEventEpoch? earlier, SessionEventEpoch? later)
    {
        if (earlier is not { } first || later is not { } second)
            return SessionEventLoss.Unknown;

        return first == second ? SessionEventLoss.NoneKnown : SessionEventLoss.Possible;
    }
}

/// <summary>
/// What <see cref="SessionEventEpoch.LossBetween"/> can tell about the events of a server between two notifications.
/// The default value is <see cref="Unknown"/>, so a verdict nobody computed never reads as complete.
/// </summary>
public enum SessionEventLoss
{
    /// <summary>At least one of the two notifications carries no mark: the SDK cannot tell.</summary>
    Unknown = 0,

    /// <summary>The two marks are equal: the SDK lost no event of that server it could know of between them.</summary>
    NoneKnown = 1,

    /// <summary>The two marks differ: the SDK may have lost an event of that server between them.</summary>
    Possible = 2,
}
