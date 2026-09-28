namespace Verbara.Sdk.Agi.Tests.TestSupport;

/// <summary>
/// Every test that asserts on a measurement of the <c>Verbara.Sdk.Agi</c> meter, such as
/// <c>agi.connections.accepted</c> or <c>agi.scripts.failed</c>, runs in this collection and nowhere
/// else.
/// </summary>
/// <remarks>
/// The instruments are process-wide and carry no tags, and this assembly runs its test classes in
/// parallel: every other test that serves a connection moves the same counters, so an assertion that
/// one moved by exactly one, or did not move, is only sound while nothing else runs. xunit runs a
/// collection with parallelization disabled on its own, after the parallel ones. xunit applies
/// <c>[Collection]</c> per class, so a counter test cannot live in a class that is not in this
/// collection.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgiMetricsGroup
{
    /// <summary>The collection's name, for <c>[Collection(AgiMetricsGroup.Name)]</c>.</summary>
    public const string Name = "AgiMetrics";
}
