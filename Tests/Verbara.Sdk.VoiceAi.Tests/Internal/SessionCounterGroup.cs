using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// Groups every class that runs a <c>VoiceAiPipeline</c> session, so they never run concurrently.
/// </summary>
/// <remarks>
/// <c>voiceai.sessions.started</c>, <c>.completed</c> and <c>.failed</c> are untagged statics on a
/// process-wide <c>Meter</c>. Two pipeline classes running in parallel would add to the same
/// counters, and a test asserting <c>failed == 0</c> would fail on someone else's session. Joining
/// one collection is the cheapest fix that keeps the rest of the assembly parallel — the
/// alternative, <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>, would
/// serialise classes that emit nothing at all.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SessionCounterGroup
{
    /// <summary>The collection name. Apply with <c>[Collection(SessionCounterGroup.Name)]</c>.</summary>
    public const string Name = "voiceai-session-counters";
}
