using System.Runtime.CompilerServices;
using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi.Testing;

/// <summary>
/// A fake <see cref="SpeechRecognizer"/> that returns pre-configured transcripts.
/// Useful for testing Voice AI pipelines without a real STT engine or API key.
/// </summary>
public sealed class FakeSpeechRecognizer : SpeechRecognizer
{
    /// <inheritdoc />
    public override string ProviderName => "Fake";

    private readonly List<(string Transcript, float Confidence)> _transcripts = [];
    private Exception? _error;
    private int _errorAfterCount;
    private TimeSpan _delay;
    private int _callIndex;
    private readonly List<int> _receivedFrameCounts = [];

    /// <summary>Number of times <see cref="StreamAsync"/> has been called.</summary>
    public int CallCount => _callIndex;

    /// <summary>Frame counts received in each call to <see cref="StreamAsync"/>.</summary>
    public IReadOnlyList<int> ReceivedFrameCounts => _receivedFrameCounts;

    /// <summary>Configures a single transcript to return on every call.</summary>
    public FakeSpeechRecognizer WithTranscript(string transcript, float confidence = 1.0f)
    {
        _transcripts.Add((transcript, confidence));
        return this;
    }

    /// <summary>Configures multiple transcripts that cycle round-robin across calls.</summary>
    public FakeSpeechRecognizer WithTranscripts(IEnumerable<string> transcripts)
    {
        foreach (var t in transcripts)
            _transcripts.Add((t, 1.0f));
        return this;
    }

    /// <summary>Adds an artificial delay before returning results.</summary>
    public FakeSpeechRecognizer WithDelay(TimeSpan delay) { _delay = delay; return this; }

    /// <summary>Configures an exception to throw, optionally after N successful calls.</summary>
    public FakeSpeechRecognizer WithError(Exception exception, int afterCount = 0)
    {
        _error = exception;
        _errorAfterCount = afterCount;
        return this;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int frameCount = 0;
        await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            frameCount++;
        _receivedFrameCounts.Add(frameCount);

        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct).ConfigureAwait(false);

        if (_error != null && _callIndex >= _errorAfterCount)
        {
            _callIndex++;
            throw _error;
        }

        if (_transcripts.Count > 0)
        {
            var (transcript, confidence) = _transcripts[_callIndex % _transcripts.Count];
            _callIndex++;
            yield return new SpeechRecognitionResult(transcript, confidence, true, TimeSpan.Zero);
        }
        else
        {
            _callIndex++;
        }
    }
}
