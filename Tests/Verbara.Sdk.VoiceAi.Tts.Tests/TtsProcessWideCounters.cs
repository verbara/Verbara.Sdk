using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// The classes that read a process-wide counter a test of theirs also moves: the synthesis meter's
/// (<c>Verbara.Sdk.VoiceAi.Tts</c>) <c>tts.frames.unparseable</c>. xunit runs a collection's tests one at a
/// time, so a count read here is this test's own.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TtsProcessWideCounters
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Tts process-wide counters";
}
