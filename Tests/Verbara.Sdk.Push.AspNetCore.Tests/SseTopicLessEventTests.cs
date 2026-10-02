namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Net;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

/// <summary>
/// An event published without a topic path is matched against a stream's allowed patterns by its event type: it
/// reaches a stream only through a pattern that matches its type, or only catch-all (<c>**</c>) streams when the
/// type is not a topic. Every read ends on a sentinel published last and allowed on every stream, so what was not
/// delivered is asserted by its absence before the sentinel; every name is read off the wire's <c>event:</c> lines.
/// </summary>
public sealed class SseTopicLessEventTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string Sentinel = "queue.9.sentinel";

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNotDeliverATopicLessEventOfADeniedTopic_WhenTheAdmissionIsPartial(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static p => p.StartsWith("queue.", StringComparison.Ordinal));
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO, Authorizer = authorizer });
        await using var client = await OpenAsync(host, "?topic=queue.**&topic=billing.**");
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync("billing.invoice.created");
        await host.PublishAsync("queue.a.updated");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK);
            client.Response?.Headers.TryGetValues(SsePushEndpoints.DeniedTopicsHeader, out _).Should().BeTrue("billing.** was denied and named in the header");
            subscribed.Should().BeTrue();
            saw.Should().BeTrue("the sentinel is allowed on the stream");
            names.Should().Equal(["queue.a.updated"], "a topic-less billing event never leaks through a partial admission that denied billing.**");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverATopicLessEventOnlyToTheStreamWhosePatternMatchesItsType_WhenStreamsAreNarrow(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var queueA = await OpenAsync(host, "?topic=queue.a.**&topic=" + Sentinel);
        await using var agentU1 = await OpenAsync(host, "?topic=agent.u1.**&topic=" + Sentinel);
        await using var billing = await OpenAsync(host, "?topic=billing.**&topic=" + Sentinel);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 3, Bound);

        await host.PublishTopicLessAsync("billing.invoice.created");
        await host.PublishAsync(Sentinel);
        var (sawQueue, namesQueue) = await queueA.ReadUntilAsync(Sentinel, Bound);
        var (sawAgent, namesAgent) = await agentU1.ReadUntilAsync(Sentinel, Bound);
        var (sawBilling, namesBilling) = await billing.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            sawQueue.Should().BeTrue();
            sawAgent.Should().BeTrue();
            sawBilling.Should().BeTrue();
            namesQueue.Should().BeEmpty("queue.a.** does not match the event type billing.invoice.created");
            namesAgent.Should().BeEmpty("agent.u1.** does not match the event type billing.invoice.created");
            namesBilling.Should().Equal(["billing.invoice.created"], "billing.** matches the event type, and the frame is named by it");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverATopicLessEventOfItsOwnUser_WhenThePatternUsesTheSelfPlaceholder(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, "?topic=agent.{self}.**&topic=" + Sentinel);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync("agent.u1.state");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["agent.u1.state"], "{self} resolves to the subscriber's user, u1");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNotDeliverATopicLessEventOfAnotherUser_WhenThePatternUsesTheSelfPlaceholder(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, "?topic=agent.{self}.**&topic=" + Sentinel);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync("agent.u2.state");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().BeEmpty("agent.{self}.** resolves to agent.u1.**, which does not match the event type agent.u2.state");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverATopicLessEventWhoseTypeIsTheGapName_WhenTheStreamIsCatchAll(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, string.Empty);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync(".gap");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["%2Egap"], "a type that is not a topic reaches a ** stream, under the escaped name");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNotDeliverATopicLessEventWhoseTypeIsTheGapName_WhenTheStreamIsNotCatchAll(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, "?topic=queue.**");
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync(".gap");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().BeEmpty("a type that is not a topic reaches only ** streams");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverAnEventNamedByItsType_WhenItsTopicPathIsEmpty(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, "?topic=billing.**&topic=" + Sentinel);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTypedAsync("billing.invoice.created", string.Empty);
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["billing.invoice.created"], "an empty topic path is treated as none: matched and named by the event type");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldKeepTenantAndUserIsolation_WhenEventsHaveNoTopicPath(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var t1u1 = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1")), ct), Bound);
        await using var t1u2 = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u2")), ct), Bound);
        await using var t2 = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, SseTestHost.Principal(null, ("tenantId", "T2"), ("sub", "u1")), ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 3, Bound);

        await host.PublishTopicLessAsync("agent.u2.note", tenant: "T1", user: "u2");
        await host.PublishTopicLessAsync("queue.t2.note", tenant: "T2");
        await host.PublishAsync(Sentinel, tenant: "T1");
        await host.PublishAsync(Sentinel, tenant: "T2");
        var (sawU1, namesU1) = await t1u1.ReadUntilAsync(Sentinel, Bound);
        var (sawU2, namesU2) = await t1u2.ReadUntilAsync(Sentinel, Bound);
        var (sawT2, namesT2) = await t2.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            sawU1.Should().BeTrue();
            sawU2.Should().BeTrue();
            sawT2.Should().BeTrue();
            namesU1.Should().BeEmpty("the T1 event targeted at u2 is not u1's, and the T2 broadcast is another tenant's");
            namesU2.Should().Equal(["agent.u2.note"], "only u2 receives the event targeted at u2");
            namesT2.Should().Equal(["queue.t2.note"], "only the T2 client receives T2's broadcast");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverATopicLessEventWhoseTypeDoesNotParse_WhenTheStreamIsCatchAll(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, string.Empty);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync("billing..x");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["billing..x"], "a type that is not a topic reaches a ** stream");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNotMatchATopicLessEventByTheTextOfAnUnparseableType_WhenTheStreamIsNarrow(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await OpenAsync(host, "?topic=billing.**&topic=" + Sentinel);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTopicLessAsync("billing..x");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().BeEmpty("billing..x is not a topic, so it reaches only ** streams, never billing.** by its text");
        }
    }

    private static Task<SseStreamClient> OpenAsync(SseTestHost host, string query) =>
        SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(query, ct), Bound);
}

/// <summary>
/// An event whose topic path is not empty and does not parse reaches no stream (catch-all streams included, and
/// never through its event type), and is reported by one <c>Warning</c> per event whatever the number of streams
/// that evaluated it, its event type and topic path percent-encoded.
/// </summary>
public sealed class SseUnparseableTopicPathTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string Sentinel = "queue.9.sentinel";

    /// <summary>The event id of the stream's report of a dropped event whose topic path does not parse.</summary>
    private const int UnparseableTopicEventId = 4;

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverNoEventWhoseTopicPathDoesNotParse_WhenEveryStreamIsCatchAll(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        var (subscribed, results) = await PublishToThreeCatchAllStreamsAsync(host, "a..b");

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            results.Should().HaveCount(3);
            results.Select(static r => r.Saw).Should().AllBeEquivalentTo(true, "every stream receives the sentinel");
            results.SelectMany(static r => r.Names).Should().BeEmpty("an unparseable topic path reaches no stream, ** included");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldLogOneWarningPerEvent_WhenItsTopicPathDoesNotParse(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        var (subscribed, results) = await PublishToThreeCatchAllStreamsAsync(host, "a..b");
        var warnings = Reports(host);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            results.Select(static r => r.Saw).Should().AllBeEquivalentTo(true, "every stream receives the sentinel");
            warnings.Should().ContainSingle("one Warning per dropped event, though three streams evaluated it");
            warnings.Should().OnlyContain(static l => l.Level == LogLevel.Warning);
            warnings.Should().OnlyContain(static l => l.Message.Contains("a..b", StringComparison.Ordinal) && l.Message.Contains("T1", StringComparison.Ordinal),
                "the report names the topic path and the tenant");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldPercentEncodeCrLfInTheReportedTopicPath_WhenItDoesNotParse(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTypedAsync("sse.test", "a..b\r\nforged");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);
        var warnings = Reports(host);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().BeEmpty();
            warnings.Should().ContainSingle("one Warning for the dropped event");
            warnings.Should().OnlyContain(static l => l.Message.Contains("a..b%0D%0Aforged", StringComparison.Ordinal),
                "CR and LF are written as %0D and %0A, so the topic path cannot forge a log line");
            warnings.Should().NotContain(static l => l.Message.Contains('\r', StringComparison.Ordinal) || l.Message.Contains('\n', StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNotFallBackToTheEventType_WhenTheTopicPathDoesNotParse(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync("?topic=billing.**&topic=" + Sentinel, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTypedAsync("billing.invoice.created", "a..b");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().BeEmpty("an unparseable topic path is dropped, never matched by the event type billing.invoice.created");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldLogNothing_WhenNoStreamEvaluatedTheUnparseableEvent(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishTypedAsync("sse.test", "a..b", user: "u9");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().BeEmpty("the client receives the sentinel only");
            Reports(host).Should().BeEmpty("the event was targeted at u9, so no open stream evaluated it");
        }
    }

    /// <summary>The stream's reports of dropped events whose topic path does not parse.</summary>
    private static List<CapturedLog> Reports(SseTestHost host) =>
    [
        .. host.Logs.Snapshot().Where(static l =>
            string.Equals(l.Category, SsePushLog.Category, StringComparison.Ordinal)
            && l.EventId.Id == UnparseableTopicEventId),
    ];

    private static async Task<(bool Subscribed, List<(bool Saw, IReadOnlyList<string> Names)> Results)> PublishToThreeCatchAllStreamsAsync(SseTestHost host, string topicPath)
    {
        await using var first = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, ct), Bound);
        await using var second = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, ct), Bound);
        await using var third = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 3, Bound);

        await host.PublishTypedAsync("queue.a.updated", topicPath);
        await host.PublishAsync(Sentinel);

        var results = new List<(bool Saw, IReadOnlyList<string> Names)>();
        foreach (var client in new[] { first, second, third })
            results.Add(await client.ReadUntilAsync(Sentinel, Bound));
        return (subscribed, results);
    }
}
