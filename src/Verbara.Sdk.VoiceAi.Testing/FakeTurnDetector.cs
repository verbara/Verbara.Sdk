namespace Verbara.Sdk.VoiceAi.Testing;

/// <summary>
/// A fake <see cref="ITurnDetector"/> that returns pre-configured signals.
/// Useful for testing pipeline behavior with deterministic turn boundaries.
/// </summary>
public sealed class FakeTurnDetector : ITurnDetector
{
    private readonly List<TurnSignal> _signals = [];
    private int _callIndex;
    private TurnSignal _default = new(TurnAction.Continue);

    /// <summary>Number of times <see cref="Analyze"/> has been called.</summary>
    public int CallCount => _callIndex;

    /// <summary>Number of times <see cref="Reset"/> has been called.</summary>
    public int ResetCount { get; private set; }

    /// <summary>Enqueue a signal to return on the next call.</summary>
    public FakeTurnDetector WithSignal(TurnAction action, float confidence = 1.0f)
    {
        _signals.Add(new TurnSignal(action, confidence));
        return this;
    }

    /// <summary>Enqueue multiple signals to return in sequence.</summary>
    public FakeTurnDetector WithSignals(IEnumerable<TurnAction> actions)
    {
        foreach (var a in actions)
            _signals.Add(new TurnSignal(a));
        return this;
    }

    /// <summary>Set the default signal returned after all enqueued signals are consumed.</summary>
    public FakeTurnDetector WithDefault(TurnAction action)
    {
        _default = new TurnSignal(action);
        return this;
    }

    /// <inheritdoc />
    public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
    {
        var signal = _callIndex < _signals.Count
            ? _signals[_callIndex]
            : _default;
        _callIndex++;
        return signal;
    }

    /// <inheritdoc />
    public void Reset() => ResetCount++;
}
