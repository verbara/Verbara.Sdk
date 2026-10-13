using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// Spec <c>webhook-event-routing</c>, requirement "The delivery service is subscribed when its start completes",
/// scenario "The subscription is held off the starting call".
/// </summary>
[Collection(WebhookProcessWideGroup.Name)]
public sealed class WebhookDeliveryStartupTests
{
    private const string FirstEvent = "startup.e1";
    private const string SecondEvent = "startup.e2";

    [Fact]
    public async Task StartAsync_ShouldBeSubscribed_WhenItCompletes()
    {
        // Declared before the run, so it is disposed after the run has stopped the service.
        using var hold = new ManualResetEventSlim(false);
        StartCallGatedBus? gated = null;
        await using var run = GuardRun.Create(
            GuardRun.ValidOptions(), HttpStatusCode.OK, [GuardRun.Subscription("s-startup", "startup.**")],
            inner => gated = new StartCallGatedBus(inner, hold));
        var e1Dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatchSignal = run.InnerBus.AsObservable().Subscribe(new EventObserver(e =>
        {
            if (e.EventType == FirstEvent)
                e1Dispatched.TrySetResult();
        }));

        bool e1WasDispatched, subscribed, e1Delivered, e2Delivered;
        int heldSubscriptions;
        try
        {
            await StartCallGatedBus.CallStartAsync(() => run.StartServiceAsync(CancellationToken.None));

            await run.PublishAsync(FirstEvent, topicPath: "startup.evt");
            e1WasDispatched = await Task.WhenAny(e1Dispatched.Task, Task.Delay(GuardRun.Bound)) == e1Dispatched.Task; // fence-allow: GUARD-TIMEOUT — failure bound on a dispatch signal, never the winning arm of a green run
        }
        finally
        {
            gated!.Release();
        }

        subscribed = await Task.WhenAny(run.Bus.Subscribed, Task.Delay(GuardRun.Bound)) == run.Bus.Subscribed; // fence-allow: GUARD-TIMEOUT — failure bound on a subscription signal, never the winning arm of a green run
        await run.PublishAsync(SecondEvent, topicPath: "startup.evt");
        e2Delivered = await run.Endpoint.To("s-startup", SecondEvent).WaitForAsync(1, GuardRun.Bound);
        e1Delivered = await run.Endpoint.To("s-startup", FirstEvent).WaitForAsync(1, GuardRun.Bound);
        heldSubscriptions = gated.Held;

        using var scope = new AssertionScope();
        e1WasDispatched.Should().BeTrue("the bus dispatched e1 to the test's own observer");
        subscribed.Should().BeTrue("the service is subscribed once the hold is released");
        e2Delivered.Should().BeTrue("e2 is published after the service has subscribed");
        e1Delivered.Should().BeTrue(
            "e1 is published after StartAsync completed, so the service must already be subscribed "
            + "(subscriptions held off the start call: {0})", heldSubscriptions);
    }
}

/// <summary>
/// An <see cref="IPushEventBus"/> decorator that holds every subscription made off the start call until
/// <see cref="Release"/>. "Off the start call" is a thread-static flag that <see cref="CallStartAsync"/> sets
/// immediately before and clears immediately after its synchronous call: a background work item queued by the
/// start cannot run on that thread while it is inside the call. An async-local flag would flow into such a
/// work item, and a thread id could be reused by it once the call has returned. The caller owns the hold.
/// </summary>
internal sealed class StartCallGatedBus(IPushEventBus inner, ManualResetEventSlim hold) : IPushEventBus
{
    private static readonly TimeSpan HoldBound = TimeSpan.FromSeconds(30);

    [ThreadStatic]
    private static bool _inStartCall;

    private int _held;

    /// <summary>How many subscriptions were made off the start call (and so were held).</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Runs <paramref name="start"/> with the flag set for exactly its synchronous part.</summary>
    public static Task CallStartAsync(Func<Task> start)
    {
        Task task;
        _inStartCall = true;
        try
        {
            task = start();
        }
        finally
        {
            _inStartCall = false;
        }
        return task;
    }

    public void Release() => hold.Set();

    public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent => inner.PublishAsync(pushEvent, ct);

    public IObservable<PushEvent> AsObservable() => new Gated<PushEvent>(inner.AsObservable(), this);

    public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => new Gated<TEvent>(inner.OfType<TEvent>(), this);

    private void HoldUnlessInStartCall()
    {
        if (_inStartCall)
            return;
        Interlocked.Increment(ref _held);
        // A bounded hold: the test always releases it; the bound only keeps a broken test from hanging the run.
        hold.Wait(HoldBound);
    }

    private sealed class Gated<T>(IObservable<T> source, StartCallGatedBus owner) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            owner.HoldUnlessInStartCall();
            return source.Subscribe(observer);
        }
    }
}
