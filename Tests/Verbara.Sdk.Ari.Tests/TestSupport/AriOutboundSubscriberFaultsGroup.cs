namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// Every test that asserts on a measurement of <c>ari.outbound.subscriber_faults</c> runs in this collection.
/// </summary>
/// <remarks>
/// The counter is process-wide and is not tagged by listener or connection, and this assembly runs its test classes in
/// parallel: any other test whose observer of an outbound listener throws moves the same counter. xunit runs a
/// collection with parallelization disabled on its own, after the parallel ones.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AriOutboundSubscriberFaultsGroup
{
    /// <summary>The collection's name, for <c>[Collection(AriOutboundSubscriberFaultsGroup.Name)]</c>.</summary>
    public const string Name = "AriOutboundSubscriberFaults";
}
