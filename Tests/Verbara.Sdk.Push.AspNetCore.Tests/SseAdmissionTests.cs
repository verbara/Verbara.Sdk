namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Net;
using FluentAssertions.Execution;

/// <summary>
/// Spec <c>push-sse-admission</c>: the measured scenarios of C7 (S0a–S5, X2–X4) on a real Kestrel host,
/// each with <c>AllowSynchronousIO</c> false (the default) and true (design D7).
/// </summary>
public sealed class SseAdmissionTests
{
    private static readonly TimeSpan HeadersBound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(5);

    private const string Sentinel = "queue.9.sentinel";

    // Published in every scenario. The subscriber is tenant T1, user u1.
    private static readonly string[] Broadcasts = ["queue.1.updated", "billing.invoice.created", "agent.7.state", "cluster.node.down"];
    private const string OtherUserEvent = "agent.99.private"; // T1, targeted at user u99
    private const string OtherTenantEvent = "queue.2.updated"; // tenant T2

    private static bool AllowAll(string p) => true;

    private static bool OnlyStarStar(string p) => p == "**";

    private static bool DenyBillingAndQueue(string p) =>
        !p.StartsWith("billing.", StringComparison.Ordinal) && !p.StartsWith("queue.", StringComparison.Ordinal);

    private static bool OnlyAgentU1(string p) => p.StartsWith("agent.u1.", StringComparison.Ordinal);

    private static bool OnlyQueue(string p) => p.StartsWith("queue.", StringComparison.Ordinal);

    /// <summary>One measured row of C7.md and what the spec says it is answered.</summary>
    private sealed record Row(
        string Query,
        Func<string, bool> Allow,
        HttpStatusCode Status,
        string[] Asked,
        string[] Events,
        string? DeniedHeader);

    private static readonly Dictionary<string, Row> Rows = new(StringComparer.Ordinal)
    {
        ["S0a"] = new(string.Empty, AllowAll, HttpStatusCode.OK, ["**"], Broadcasts, null),
        ["S0b"] = new("?topic=billing.**", OnlyStarStar, HttpStatusCode.Forbidden, ["billing.**"], [], null),
        ["S1a"] = new("?topic=billing.**", DenyBillingAndQueue, HttpStatusCode.Forbidden, ["billing.**"], [], null),
        ["S1b"] = new("?topic=queue.**&topic=billing.**", OnlyStarStar, HttpStatusCode.Forbidden, ["queue.**", "billing.**"], [], null),
        ["S2"] = new("?topic=billing.**", OnlyAgentU1, HttpStatusCode.Forbidden, ["billing.**"], [], null),
        ["S3a"] = new(string.Empty, AllowAll, HttpStatusCode.OK, ["**"], Broadcasts, null),
        ["S3b"] = new(string.Empty, OnlyAgentU1, HttpStatusCode.Forbidden, ["**"], [], null),
        ["S4"] = new("?topic=queue.**&topic=billing.**", OnlyQueue, HttpStatusCode.OK, ["queue.**", "billing.**"], ["queue.1.updated"], "billing.%2A%2A"),
        ["S5"] = new("?topic=a..b", AllowAll, HttpStatusCode.BadRequest, [], [], null),
        ["X2"] = new("?topic=queue.**&topic=a..b", AllowAll, HttpStatusCode.BadRequest, [], [], null),
        ["X3"] = new("?topic=a..b&topic=billing.**", DenyBillingAndQueue, HttpStatusCode.BadRequest, [], [], null),
        ["X4"] = new("?topic=queue.**&topic=bill%0D%0Aing%2Cx.**", OnlyQueue, HttpStatusCode.OK, ["queue.**", "bill\r\ning,x.**"], ["queue.1.updated"], "bill%0D%0Aing%2Cx.%2A%2A"),
    };

    public static TheoryData<string, bool> Scenarios()
    {
        var data = new TheoryData<string, bool>();
        foreach (var id in Rows.Keys)
        {
            data.Add(id, false);
            data.Add(id, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task StreamRequest_ShouldBeServedOnlyWhatWasAskedAndAllowed_WhenMeasuredScenarioRuns(string row, bool allowSynchronousIO)
    {
        var expected = Rows[row];
        var authorizer = new RecordingAuthorizer(expected.Allow);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            BusCapacity = 64, // above the 7 events published (design D7)
        });

        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(expected.Query, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(HeadersBound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;

        var status = response?.StatusCode;
        var contentType = response?.Content.Headers.ContentType?.MediaType;
        string? deniedHeader = null;
        if (response is not null && response.Headers.TryGetValues("X-Push-Denied-Topics", out var denied))
            deniedHeader = string.Join("|", denied);

        // An admitted stream subscribes after its headers: wait for the subscription by count before publishing.
        var subscribed = status == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, HeadersBound);

        foreach (var topic in Broadcasts)
            await host.PublishAsync(topic);
        await host.PublishAsync(OtherUserEvent, user: "u99");
        await host.PublishAsync(OtherTenantEvent, tenant: "T2");
        await host.PublishAsync(Sentinel); // matches every admitted row; ends the read by count

        var received = new List<string>();
        var body = string.Empty;
        if (response is not null && status == HttpStatusCode.OK)
        {
            using var reader = new SseReader(await response.Content.ReadAsStreamAsync(abort.Token));
            await reader.ReadUntilAsync(static f => f.EventName == Sentinel, ReadBound);
            received.AddRange(reader.Frames.Select(static f => f.EventName).OfType<string>().Where(static n => n != Sentinel));
        }
        else if (response is not null)
        {
            body = await response.Content.ReadAsStringAsync(abort.Token).WaitAsync(ReadBound);
        }

        await abort.CancelAsync();

        using (new AssertionScope($"{row} (AllowSynchronousIO={allowSynchronousIO})"))
        {
            headersArrived.Should().BeTrue("the response headers must arrive within the bound");
            status.Should().Be(expected.Status);
            authorizer.Asked.Should().Equal(expected.Asked, "the authorizer is asked exactly about what the client named (or ** for no topic), and nothing for an unparseable request");
            received.Should().Equal(expected.Events, "the client receives exactly the events it asked for and was allowed");
            received.Should().NotContain([OtherTenantEvent, OtherUserEvent], "tenant and user isolation hold under every admission outcome");
            host.LoggedExceptions.Select(static l => l.Exception!.GetType().Name + ": " + l.Exception.Message)
                .Should().BeEmpty("the host logs no exception for any admission outcome");

            if (expected.Status == HttpStatusCode.OK)
            {
                contentType.Should().Be("text/event-stream");
                subscribed.Should().BeTrue("an admitted stream subscribes to the bus");
                deniedHeader.Should().Be(expected.DeniedHeader, "X-Push-Denied-Topics lists the denied topics percent-encoded, and is absent when nothing was denied");
            }
            else
            {
                contentType.Should().NotBe("text/event-stream", "a refusal is written before any event-stream byte");
                host.Bus.Subscribers.Value.Should().Be(0, "a refused request never subscribes to the bus");
                body.Should().NotContain("internal rule 42", "a refusal does not echo the authorizer's reason");
            }
        }
    }
}
