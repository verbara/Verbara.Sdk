using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// Spec <c>webhook-event-routing</c>, requirement "The delivery service is subscribed when its start completes":
/// what stopping, disposing, restarting and cancelling the start token do to delivery. A negative is closed by
/// <see cref="GuardRun.DrainAsync"/>: once a marker published later has been dispatched, the earlier event has been
/// handed to every observer it was going to reach.
/// </summary>
[Collection(WebhookProcessWideGroup.Name)]
public sealed class WebhookDeliveryLifetimeTests
{
    private const string Before = "h118.before";
    private const string After = "h118.after";

    [Fact]
    public async Task StopAsync_ShouldDispatchNothingAfterwards_WhenAnEventIsPublishedAfterStop()
    {
        using var unobserved = new UnobservedTaskExceptions();
        bool deliveredBefore, drained;
        int enumerationsAtStop, enumerationsAfter, deliveredAfter;
        await using (var run = await StartAsync())
        {
            await run.PublishAsync(Before, topicPath: "h118.evt");
            deliveredBefore = await run.Endpoint.To("s-h118", Before).WaitForAsync(1, GuardRun.Bound);
            await run.StopServiceAsync();
            enumerationsAtStop = run.Store.Enumerations;

            await run.PublishAsync(After, topicPath: "h118.evt");
            drained = await run.DrainAsync();
            enumerationsAfter = run.Store.Enumerations;
            deliveredAfter = run.Endpoint.To("s-h118", After).Count;
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        deliveredBefore.Should().BeTrue("the event published before the stop is delivered");
        drained.Should().BeTrue("the marker published after the event is dispatched");
        enumerationsAfter.Should().Be(enumerationsAtStop, "a stopped service dispatches nothing, so it never enumerates its subscriptions");
        deliveredAfter.Should().Be(0, "nothing is delivered for an event published after the stop");
        faulted.Should().BeEmpty("no task faults unobserved");
    }

    [Fact]
    public async Task Dispose_ShouldDeliverNothingAfterwards_WhenDisposedWithoutAStop()
    {
        using var unobserved = new UnobservedTaskExceptions();
        bool deliveredBefore, drained;
        int deliveredAfter;
        await using (var run = await StartAsync())
        {
            await run.PublishAsync(Before, topicPath: "h118.evt");
            deliveredBefore = await run.Endpoint.To("s-h118", Before).WaitForAsync(1, GuardRun.Bound);
            run.DisposeService();

            // The bus outlives the service: it is disposed only when the run is.
            await run.PublishAsync(After, topicPath: "h118.evt");
            drained = await run.DrainAsync();
            deliveredAfter = run.Endpoint.To("s-h118", After).Count;
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        deliveredBefore.Should().BeTrue("the event published before the dispose is delivered");
        drained.Should().BeTrue("the marker published after the event is dispatched");
        deliveredAfter.Should().Be(0, "a disposed service delivers nothing, even without a stop");
        faulted.Should().BeEmpty("no task faults unobserved");
    }

    [Fact]
    public async Task StartAsync_ShouldDeliverAgain_WhenTheServiceWasStoppedAndStartedAgain()
    {
        using var unobserved = new UnobservedTaskExceptions();
        bool resubscribed, delivered;
        await using (var run = await StartAsync())
        {
            await run.StopServiceAsync();
            await run.StartServiceAsync(CancellationToken.None);
            resubscribed = await run.Bus.Subscriptions.WaitForAsync(2, GuardRun.Bound);

            await run.PublishAsync(After, topicPath: "h118.evt");
            delivered = await run.Endpoint.To("s-h118", After).WaitForAsync(1, GuardRun.Bound);
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        resubscribed.Should().BeTrue("the second start subscribes again");
        delivered.Should().BeTrue("a service stopped and started again delivers again");
        faulted.Should().BeEmpty("no task faults unobserved");
    }

    [Fact]
    public async Task Delivery_ShouldContinue_WhenTheStartTokenIsCancelledAfterStart()
    {
        using var unobserved = new UnobservedTaskExceptions();
        bool delivered;
        using (var startToken = new CancellationTokenSource())
        await using (var run = GuardRun.Create(GuardRun.ValidOptions(), HttpStatusCode.OK, [GuardRun.Subscription("s-h118", "h118.**")]))
        {
            await run.StartServiceAsync(startToken.Token);
            await run.Bus.Subscribed.WaitAsync(GuardRun.Bound);
            await startToken.CancelAsync();

            await run.PublishAsync(After, topicPath: "h118.evt");
            delivered = await run.Endpoint.To("s-h118", After).WaitForAsync(1, GuardRun.Bound);
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        delivered.Should().BeTrue("the start token governs the start only; cancelling it afterwards does not end delivery");
        faulted.Should().BeEmpty("no task faults unobserved");
    }

    private static async Task<GuardRun> StartAsync()
    {
        var run = GuardRun.Create(GuardRun.ValidOptions(), HttpStatusCode.OK, [GuardRun.Subscription("s-h118", "h118.**")]);
        await run.StartServiceAsync(CancellationToken.None);
        await run.Bus.Subscribed.WaitAsync(GuardRun.Bound);
        return run;
    }
}
