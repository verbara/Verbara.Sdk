using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi;

namespace VoiceAiCustomProviderExample;

/// <summary>
/// Demonstrates the minimum surface of a custom <see cref="SpeechRecognizer"/>:
/// override <see cref="StreamAsync"/> to stream audio to your backend and yield
/// results, and override <see cref="ProviderName"/> with a stable literal so
/// the pipeline's STT activity tags don't fall back to the (slower, reflective)
/// <c>GetType().Name</c>.
///
/// This sample just counts frames and yields a canned transcript — replace the
/// body with a real STT backend (gRPC/WebSocket to your in-house service, an
/// edge model, a regional provider, etc.).
/// </summary>
public sealed class MyEchoRecognizer : SpeechRecognizer
{
    /// <inheritdoc />
    public override string ProviderName => "MyEcho";

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int frames = 0;
        await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            frames++;

        // Emit a single final result with the frame count as the transcript.
        yield return new SpeechRecognitionResult(
            Transcript: $"received {frames} frame(s)",
            Confidence: 1.0f,
            IsFinal: true,
            Duration: TimeSpan.FromMilliseconds(frames * 20));
    }
}
