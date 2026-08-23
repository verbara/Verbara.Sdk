using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// Yields one chunk, then parks until released or cancelled.
/// </summary>
/// <remarks>
/// Parking after the first chunk is what makes "a synthesis is in flight" a fact rather than a
/// probability: the pipeline's synthesis token is assigned before the enumeration starts and
/// released in the <c>finally</c> that only runs once the enumeration ends, so anything the test
/// does between <see cref="Parked"/> and <see cref="Release"/> happens inside that window.
/// </remarks>
internal sealed class ParkingSpeechSynthesizer : SpeechSynthesizer
{
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override string ProviderName => "Parking";

    /// <summary>Completes once the first chunk has been consumed and the synthesis is parked.</summary>
    public Task Parked => _parked.Task;

    /// <summary>Ends the park. Safe to call when the synthesis was already cancelled.</summary>
    public void Release() => _release.TrySetResult();

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new byte[320];
        _parked.TrySetResult();
        await _release.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        Release();
        return base.DisposeAsync();
    }
}
