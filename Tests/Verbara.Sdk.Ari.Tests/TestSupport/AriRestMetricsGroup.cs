namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// Every test that asserts on a measurement of the <c>Verbara.Sdk.Ari</c> meter's REST instruments,
/// <c>ari.rest.requests.sent</c> and <c>ari.rest.roundtrip</c>, runs in this collection and nowhere else.
/// </summary>
/// <remarks>
/// The instruments are process-wide and carry no tag, and this assembly runs its test classes in parallel: any other
/// test whose client issues a REST request moves the same counter, so an assertion that it moved by exactly one is
/// only sound while nothing else runs. xunit runs a collection with parallelization disabled on its own, after the
/// parallel ones, and applies <c>[Collection]</c> per class.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AriRestMetricsGroup
{
    /// <summary>The collection's name, for <c>[Collection(AriRestMetricsGroup.Name)]</c>.</summary>
    public const string Name = "AriRestMetrics";
}
