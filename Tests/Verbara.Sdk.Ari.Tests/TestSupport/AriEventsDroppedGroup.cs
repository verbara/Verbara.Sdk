namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// Every test that asserts on a measurement of <c>ari.events.dropped</c> runs in this collection and nowhere else.
/// </summary>
/// <remarks>
/// The counter is process-wide and its only tag is the reason, never the client, and this assembly runs its test
/// classes in parallel: any other test whose client ends with events still buffered moves the same counter. xunit
/// runs a collection with parallelization disabled on its own, after the parallel ones.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AriEventsDroppedGroup
{
    /// <summary>The collection's name, for <c>[Collection(AriEventsDroppedGroup.Name)]</c>.</summary>
    public const string Name = "AriEventsDropped";
}
