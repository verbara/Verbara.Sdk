using System.Runtime.CompilerServices;
using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi.Testing;

/// <summary>
/// A fake <see cref="SpeechSynthesizer"/> that generates silence frames or custom audio.
/// Useful for testing Voice AI pipelines without a real TTS engine or API key.
/// </summary>
public sealed class FakeSpeechSynthesizer : SpeechSynthesizer
{
    private TimeSpan _silenceDuration;
    private ReadOnlyMemory<byte>? _audioData;
    private Exception? _error;
    private int _errorAfterCount;
    private TimeSpan _delay;
    private int _callIndex;
    private readonly List<string> _synthesizedTexts = [];

    /// <summary>Number of times <see cref="SynthesizeAsync"/> has been called.</summary>
    public int CallCount => _callIndex;

    /// <summary>All texts that were passed to <see cref="SynthesizeAsync"/>, in order.</summary>
    public IReadOnlyList<string> SynthesizedTexts => _synthesizedTexts;

    /// <summary>Configures the fake to generate silence frames for the given duration (20ms per frame).</summary>
    public FakeSpeechSynthesizer WithSilence(TimeSpan duration) { _silenceDuration = duration; return this; }

    /// <summary>Configures the fake to return a single frame of custom PCM audio data.</summary>
    public FakeSpeechSynthesizer WithAudio(ReadOnlyMemory<byte> pcmAudio) { _audioData = pcmAudio; return this; }

    /// <summary>Adds an artificial delay before returning results.</summary>
    public FakeSpeechSynthesizer WithDelay(TimeSpan delay) { _delay = delay; return this; }

    /// <summary>Configures an exception to throw, optionally after N successful calls.</summary>
    public FakeSpeechSynthesizer WithError(Exception exception, int afterCount = 0)
    {
        _error = exception;
        _errorAfterCount = afterCount;
        return this;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _synthesizedTexts.Add(text);

        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct).ConfigureAwait(false);

        if (_error != null && _callIndex >= _errorAfterCount)
        {
            _callIndex++;
            throw _error;
        }

        _callIndex++;

        if (_audioData.HasValue)
        {
            yield return _audioData.Value;
            yield break;
        }

        // Generate silence frames: 20ms per frame = 160 samples @ 8kHz = 320 bytes
        int frameSizeBytes = 320;
        int totalFrames = (int)(_silenceDuration.TotalMilliseconds / 20);
        var silence = new byte[frameSizeBytes];
        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return silence.AsMemory();
        }
    }
}
