using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// Spec <c>webhook-event-routing</c>, requirement "An event without a topic path is matched by its event type".
/// Every negative is closed by a sentinel: the store answers synchronously and the endpoint counts inside the
/// send, so the routing of one event completes inside its dispatch, before the bus dispatches the next one.
/// Once the sentinel published last has been counted, a zero for an earlier event is final.
/// </summary>
[Collection(WebhookProcessWideGroup.Name)]
public sealed class WebhookTopiclessRoutingTests
{
    private const string Sentinel = "sentinel";
    private const string SentinelType = "sentinel.end";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Dispatch_ShouldMatchByEventType_WhenTopicPathIsNullOrEmptyAndTypeIsATopic(string? topicPath)
    {
        await using var run = await StartAsync(
            GuardRun.Subscription("typed", "routing.**"),
            GuardRun.Subscription("other", "calls.**"),
            GuardRun.Subscription("all", "**"));

        await run.PublishAsync("routing.evt", topicPath);
        var typed = await run.Endpoint.To("typed", "routing.evt").WaitForAsync(1, GuardRun.Bound);
        var all = await run.Endpoint.To("all", "routing.evt").WaitForAsync(1, GuardRun.Bound);
        var closed = await CloseAsync(run);

        using var scope = new AssertionScope();
        typed.Should().BeTrue("the event type routing.evt matches routing.**");
        all.Should().BeTrue("the event type routing.evt matches **");
        closed.Should().BeTrue("the sentinel published last is delivered");
        run.Endpoint.To("other").Count.Should().Be(0, "the event type routing.evt does not match calls.**");
        run.Endpoint.To("typed").Count.Should().Be(1, "routing.** receives the event once");
    }

    [Fact]
    public async Task Dispatch_ShouldDeliverOnlyToCatchAll_WhenTopicPathIsNullAndTypeIsNotATopic()
    {
        await using var run = await StartAsync(
            GuardRun.Subscription("typed", "routing.**"),
            GuardRun.Subscription("all", "**"));

        await run.PublishAsync("routing..bad", topicPath: null);
        var all = await run.Endpoint.To("all", "routing..bad").WaitForAsync(1, GuardRun.Bound);
        var closed = await CloseAsync(run);
        var errors = run.Log.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();

        using var scope = new AssertionScope();
        all.Should().BeTrue("an event type that is not a topic reaches the ** subscription");
        closed.Should().BeTrue("the sentinel published last is delivered");
        run.Endpoint.To("typed").Count.Should().Be(0, "only ** receives an event whose type is not a topic");
        errors.Should().BeEmpty("a topic-less event whose type is not a topic is not a dispatch failure");
    }

    [Fact]
    public async Task Dispatch_ShouldMatchByTopicPathAlone_WhenTopicPathIsSet()
    {
        await using var run = await StartAsync(
            GuardRun.Subscription("typed", "routing.**"),
            GuardRun.Subscription("other", "calls.**"),
            GuardRun.Subscription("all", "**"));

        await run.PublishAsync("calls.evt", topicPath: "routing.evt");
        var typed = await run.Endpoint.To("typed", "calls.evt").WaitForAsync(1, GuardRun.Bound);
        var all = await run.Endpoint.To("all", "calls.evt").WaitForAsync(1, GuardRun.Bound);
        var closed = await CloseAsync(run);

        using var scope = new AssertionScope();
        typed.Should().BeTrue("the topic path routing.evt matches routing.**");
        all.Should().BeTrue("the topic path routing.evt matches **");
        closed.Should().BeTrue("the sentinel published last is delivered");
        run.Endpoint.To("other").Count.Should().Be(0, "the topic path decides, not the event type calls.evt");
    }

    [Fact]
    public async Task Dispatch_ShouldResolveSelfFromTheEventUser_WhenTopicPathIsNull()
    {
        await using var run = await StartAsync(GuardRun.Subscription("self", "agents.{self}.**"));

        await run.PublishAsync("agents.u1.state", topicPath: null, userId: "u1");
        var own = await run.Endpoint.To("self").WaitForAsync(1, GuardRun.Bound);
        await run.PublishAsync("agents.u1.state", topicPath: null, userId: "u2");
        var closed = await CloseAsync(run);

        using var scope = new AssertionScope();
        own.Should().BeTrue("agents.u1.state addressed to u1 matches agents.{self}.**");
        closed.Should().BeTrue("the sentinel published last is delivered");
        run.Endpoint.To("self").Count.Should().Be(1, "the same event addressed to u2 does not match agents.{self}.**");
    }

    private static async Task<GuardRun> StartAsync(params WebhookSubscription[] subscriptions)
    {
        var run = GuardRun.Create(GuardRun.ValidOptions(), HttpStatusCode.OK, [.. subscriptions, GuardRun.Subscription(Sentinel, "sentinel.**")]);
        await run.StartServiceAsync(CancellationToken.None);
        return run;
    }

    // A topic-bearing event only the sentinel subscription (and a ** one) matches: once it is counted, every event
    // published before it has been routed.
    private static async Task<bool> CloseAsync(GuardRun run)
    {
        await run.PublishAsync(SentinelType, topicPath: SentinelType);
        return await run.Endpoint.To(Sentinel, SentinelType).WaitForAsync(1, GuardRun.Bound);
    }
}
