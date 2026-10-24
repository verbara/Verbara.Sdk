namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// Supplies the length of a queue member's after-call wrap-up per server and queue, for a host whose queues do not
/// all share <see cref="SessionOptions.WrapUpDuration"/>. Asterisk reports when a member's wrap-up starts (the
/// member's <c>AgentComplete</c>) but never how long a queue's <c>wrapuptime</c> is, so the host, which wrote that
/// value to Asterisk, is the only source of it.
/// </summary>
/// <remarks>
/// <para>
/// Register one implementation as a singleton in the service container, before or after the SDK's sessions
/// registration; the session manager the SDK registers uses it. A host that builds <see cref="CallSessionManager"/>
/// itself passes it to the constructor that takes one.
/// </para>
/// <para>
/// <see cref="GetWrapUpDuration"/> is called synchronously, on the thread that delivers that server's AMI events,
/// once for each queue member's <c>AgentComplete</c> that is about to open a wrap-up. It must answer from memory,
/// with no I/O and no waiting: a provider that blocks delays every later event of its server, and past the
/// connection's event buffer makes the connection drop them. A host whose values live in a database keeps an
/// in-memory copy and refreshes it where it changes them.
/// </para>
/// <para>
/// An answer longer than <see cref="SessionOptions.CompletedRetention"/> is used, but a subscriber that looks the
/// session up by id when its <see cref="CallWrapUpEvent"/> arrives may no longer find it. Do not call
/// <see cref="CallSessionManager.DetachFromServer(string)"/> from this method.
/// </para>
/// </remarks>
public interface IWrapUpDurationProvider
{
    /// <summary>
    /// Returns the wrap-up duration of queue <paramref name="queueName"/> on the server attached as
    /// <paramref name="serverId"/>, or <see langword="null"/> when the host has no value of its own for that queue,
    /// in which case <see cref="SessionOptions.WrapUpDuration"/> applies.
    /// </summary>
    /// <param name="serverId">
    /// The id under which the server that delivered the member's <c>AgentComplete</c> was attached to the session
    /// manager (<c>"default"</c> for the single server the SDK attaches itself).
    /// </param>
    /// <param name="queueName">
    /// The queue as Asterisk names it in that <c>AgentComplete</c>, neither trimmed nor case-folded; the session
    /// manager itself compares queue names without regard to case.
    /// </param>
    /// <returns>
    /// The duration to apply, or <see langword="null"/> for none. Zero closes the wrap-up as it opens. A negative
    /// duration (including <see cref="Timeout.InfiniteTimeSpan"/>), one above 4 294 967 294 ms, and an exception fall
    /// back to <see cref="SessionOptions.WrapUpDuration"/> and are logged.
    /// </returns>
    TimeSpan? GetWrapUpDuration(string serverId, string queueName);
}
