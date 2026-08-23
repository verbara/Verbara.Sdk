using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// Records everything a <see cref="VoiceAiPipeline"/> publishes, and lets a harness wait for a
/// specific event instead of guessing how long the pipeline needs.
/// </summary>
/// <remarks>
/// <para>
/// The per-frame detector signal proves the pipeline decided on a frame; it says nothing about the
/// response cycle that decision triggers. Recognition, the handler call and synthesis all run on
/// after the end-of-utterance decision, and a <c>Task.Delay</c> covering them is the barrier this
/// change exists to remove. <c>SynthesisEndedEvent</c> and <c>PipelineErrorEvent</c> are the
/// pipeline saying that cycle is over, which is the fact the harness actually needs.
/// </para>
/// <para>
/// Construct it before the session starts and it cannot miss an event: <c>Events</c> is a
/// <c>Subject</c> with no replay, so a late subscriber silently sees fewer events rather than
/// failing, which would read as a flake. Waiters left pending when the pipeline completes its
/// stream are faulted rather than abandoned, so the test names the event that never arrived
/// instead of timing out somewhere further up.
/// </para>
/// </remarks>
internal sealed class PipelineEventCapture : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private readonly List<VoiceAiPipelineEvent> _events = [];
    private readonly List<Waiter> _waiters = [];

    public PipelineEventCapture(VoiceAiPipeline pipeline) =>
        _subscription = pipeline.Events.Subscribe(OnNext, OnCompleted);

    /// <summary>A snapshot of everything published so far, oldest first.</summary>
    public IReadOnlyList<VoiceAiPipelineEvent> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    /// <summary>
    /// Completes once the <paramref name="count"/>-th <typeparamref name="T"/> has been published,
    /// counting any already seen.
    /// </summary>
    public Task WaitFor<T>(int count = 1)
        where T : VoiceAiPipelineEvent
    {
        lock (_gate)
        {
            // Remaining counts what is still owed, not what was asked for: a waiter registered
            // after some of its events already arrived must not wait for them a second time.
            var remaining = count - _events.Count(e => e is T);
            if (remaining <= 0)
                return Task.CompletedTask;

            var waiter = new Waiter(typeof(T), e => e is T, remaining);
            _waiters.Add(waiter);
            return waiter.Source.Task;
        }
    }

    /// <summary>
    /// Completes once the response cycle ends, whichever way it ends. A synthesis that fails
    /// publishes an error rather than a <see cref="SynthesisEndedEvent"/>, and a harness that waited
    /// only for the happy ending would hang on exactly the tests written to exercise failure.
    /// </summary>
    public Task WaitForResponseCycle(int count = 1)
    {
        lock (_gate)
        {
            var remaining = count - _events.Count(IsCycleEnd);
            if (remaining <= 0)
                return Task.CompletedTask;

            var waiter = new Waiter(typeof(SynthesisEndedEvent), IsCycleEnd, remaining);
            _waiters.Add(waiter);
            return waiter.Source.Task;
        }
    }

    private static bool IsCycleEnd(VoiceAiPipelineEvent evt) =>
        evt is SynthesisEndedEvent or PipelineErrorEvent;

    private void OnNext(VoiceAiPipelineEvent evt)
    {
        lock (_gate)
        {
            _events.Add(evt);

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (!waiter.Match(evt) || --waiter.Remaining > 0)
                    continue;

                waiter.Source.TrySetResult();
                _waiters.RemoveAt(i);
            }
        }
    }

    private void OnCompleted()
    {
        lock (_gate)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Source.TrySetException(new InvalidOperationException(
                    $"The pipeline completed its event stream while still waiting for " +
                    $"{waiter.Remaining} more {waiter.Expected.Name}."));
            }

            _waiters.Clear();
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        OnCompleted();
    }

    private sealed class Waiter(Type expected, Func<VoiceAiPipelineEvent, bool> match, int count)
    {
        public Type Expected { get; } = expected;
        public Func<VoiceAiPipelineEvent, bool> Match { get; } = match;
        public int Remaining = count;
        public TaskCompletionSource Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
