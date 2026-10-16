namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The collection every class of this assembly that makes a subscriber of the session notifications throw, and
/// reads the fault counters that failure may reach, runs in. Apply with
/// <c>[Collection(SessionSubscriberFaultsGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// The counters read — <c>ami.events.observer_faults</c> on the <c>Verbara.Sdk.Ami</c> meter, and the session
/// subscriber fault counter on the <c>Verbara.Sdk.Sessions</c> meter — are process-wide statics with no tags: every
/// connection and every session manager in this assembly adds to the same instruments, and a listener sees what a
/// class running beside it adds. This assembly runs its classes in parallel, and several of them drive real
/// connections whose observers may throw. So the collection is declared with <c>DisableParallelization</c>: xunit runs
/// it only after every parallel collection has finished, and its classes one at a time, so the tests can assert exact
/// counts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionSubscriberFaultsGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "session-subscriber-faults";
}
