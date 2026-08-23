namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// Wraps another <see cref="ITurnDetector"/> and announces each frame it decided on, without
/// changing a single decision.
/// </summary>
/// <remarks>
/// <para>
/// <c>VoiceAiPipeline</c> calls <see cref="Analyze"/> synchronously, once per 20 ms frame, on the
/// monitor loop's own thread. A completed <see cref="Analyzed"/> task is therefore proof that the
/// pipeline has finished reacting to that many frames — the fact a harness previously approximated
/// with <c>Task.Delay</c>.
/// </para>
/// <para>
/// It <em>decorates</em> rather than replaces because most of these tests exercise the default
/// detection path — <c>SilenceTurnDetector</c>, which the pipeline builds itself when DI carries no
/// <see cref="ITurnDetector"/>. Substituting a scripted detector would give the harness its signal
/// at the cost of no longer testing the logic the test names. Wrapping keeps both: register
/// <c>new ObservingTurnDetector(new SilenceTurnDetector(Options.Create(options)))</c> with the same
/// options the pipeline would have used, and behaviour is unchanged by construction.
/// </para>
/// </remarks>
internal sealed class ObservingTurnDetector : ITurnDetector
{
    private readonly ITurnDetector _inner;
    private readonly Lock _gate = new();
    private readonly List<(int Threshold, TaskCompletionSource Source)> _waiters = [];
    private readonly List<TurnAction> _decisions = [];
    private int _count;

    public ObservingTurnDetector(ITurnDetector inner) => _inner = inner;

    /// <summary>Frames decided on so far.</summary>
    public int AnalyzedCount
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>The decisions made so far, oldest first.</summary>
    public IReadOnlyList<TurnAction> Decisions
    {
        get { lock (_gate) return [.. _decisions]; }
    }

    /// <summary>
    /// Completes once <paramref name="frames"/> frames have been decided on. Already complete if
    /// that many have already gone through, so a harness can await it after the fact.
    /// </summary>
    public Task Analyzed(int frames)
    {
        lock (_gate)
        {
            if (_count >= frames)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((frames, tcs));
            return tcs.Task;
        }
    }

    public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
    {
        var signal = _inner.Analyze(samples, isAssistantSpeaking);

        lock (_gate)
        {
            _count++;
            _decisions.Add(signal.Action);

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Threshold > _count)
                    continue;

                _waiters[i].Source.TrySetResult();
                _waiters.RemoveAt(i);
            }
        }

        return signal;
    }

    /// <summary>
    /// Resets the wrapped detector. The observed count is deliberately <em>not</em> reset: it
    /// numbers frames the harness sent, and a harness that lost its count mid-session could no
    /// longer order anything.
    /// </summary>
    public void Reset() => _inner.Reset();
}
