using System.Runtime.CompilerServices;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// Shared audio-frame generators for the STT recognizer suites. Lives here (rather than as a
/// private copy per provider suite) so the seven-provider cancellation contract rests on one
/// non-duplicated generator with a single annotated pacer.
/// </summary>
internal static class SttFrameGenerators
{
    /// <summary>
    /// Yields 20 ms silence frames until the token is cancelled. Cancellation tests need an input
    /// stream that never completes on its own so the recognizer cannot finish for any reason other
    /// than the token: the pre-cancelled token is then observed at the first iteration boundary,
    /// which is exactly the contract <c>StreamAsync_ShouldAbort_WhenCancelled</c> asserts.
    /// </summary>
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> EndlessFrames(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return new byte[320];
            await Task.Delay(10, ct); // fence-allow: LOOP-DRIVER — paces the endless frame generator; never executes under a pre-cancelled token
        }
    }
}
