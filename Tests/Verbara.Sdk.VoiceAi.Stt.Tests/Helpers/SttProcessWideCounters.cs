using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// The classes that read process-wide counters a test of theirs also moves: the pipeline's
/// (<c>Verbara.Sdk.VoiceAi</c>) and the recognition meter's (<c>Verbara.Sdk.VoiceAi.Stt</c>). xunit runs
/// a collection's tests one at a time, so a count read here is this test's own.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SttProcessWideCounters
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Stt process-wide counters";
}
