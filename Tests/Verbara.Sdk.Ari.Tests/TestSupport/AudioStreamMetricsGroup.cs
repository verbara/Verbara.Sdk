namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// Every test that asserts on a measurement of the <c>Verbara.Sdk.Ari.Audio</c> meter, such as
/// <c>audio.transport.failures</c>, runs in this collection and nowhere else.
/// </summary>
/// <remarks>
/// The instruments are process-wide and carry no tags, and this assembly runs its test classes in
/// parallel: any other test that resets a live session moves the same counter, so an assertion that
/// it moved by exactly one, or did not move, is only sound while nothing else runs. xunit runs a
/// collection with parallelization disabled on its own, after the parallel ones. The Realtime
/// assembly made the same move for its own process-wide instruments. xunit applies
/// <c>[Collection]</c> per class, so a counter test cannot live in a class that is not in this
/// collection.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioStreamMetricsGroup
{
    /// <summary>The collection's name, for <c>[Collection(AudioStreamMetricsGroup.Name)]</c>.</summary>
    public const string Name = "AudioStreamMetrics";
}
