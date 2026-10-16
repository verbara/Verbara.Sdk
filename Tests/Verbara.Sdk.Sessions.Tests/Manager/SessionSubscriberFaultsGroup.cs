namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The collection every class that makes a subscriber of the session notifications throw, or reads how many such
/// failures were counted, runs in. Apply with <c>[Collection(SessionSubscriberFaultsGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// A subscriber's failure is counted on a process-wide static counter of the <c>Verbara.Sdk.Sessions</c> meter with
/// no tags: every manager and every agent tracker in this assembly adds to the same instrument, and a listener sees
/// what a class running beside it adds. So the collection is declared with <c>DisableParallelization</c>: xunit runs
/// it only after every parallel collection has finished, and its classes one at a time, so the tests can assert exact
/// counts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionSubscriberFaultsGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "session-subscriber-faults";
}
