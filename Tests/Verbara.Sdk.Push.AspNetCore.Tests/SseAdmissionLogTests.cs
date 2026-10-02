namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Net;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

/// <summary>
/// A refused stream request logs one <c>Warning</c> that carries the
/// authorizer's reason server-side, and an admitted or unparseable request logs no refusal.
/// </summary>
public sealed class SseAdmissionLogTests
{
    private static readonly TimeSpan HeadersBound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamRequest_ShouldLogOneWarningWithTheReason_WhenEveryTopicIsDenied(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static p => p.StartsWith("queue.", StringComparison.Ordinal));
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
        });

        using var response = await host.OpenStreamAsync("?topic=billing.**&topic=bill%0D%0Aing.**", CancellationToken.None)
            .WaitAsync(HeadersBound);
        var body = await response.Content.ReadAsStringAsync().WaitAsync(HeadersBound);

        var refusals = host.Logs.Snapshot()
            .Where(static l => string.Equals(l.Category, SsePushLog.Category, StringComparison.Ordinal))
            .ToList();

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            body.Should().Be(SsePushEndpoints.SubscriptionDeniedBody, "the 403 body is fixed text");
            refusals.Should().ContainSingle("one refused request is one log entry");
            refusals[0].Level.Should().Be(LogLevel.Warning, "the SDK logs handled client refusals at Warning");
            refusals[0].Message.Should().Contain(RecordingAuthorizer.DenyReason, "the authorizer's reason is kept server-side");
            refusals[0].Message.Should().Contain("tenant=T1").And.Contain("billing.%2A%2A,bill%0D%0Aing.%2A%2A",
                "the denied topics are logged percent-encoded, so a CR/LF in a topic never splits the log line");
        }
    }

    [Theory]
    [InlineData("?topic=a..b", HttpStatusCode.BadRequest)]
    [InlineData("?topic=queue.**&topic=billing.**", HttpStatusCode.OK)]
    public async Task StreamRequest_ShouldLogNoRefusal_WhenTheRequestIsNotDenied(string query, HttpStatusCode expected)
    {
        var authorizer = new RecordingAuthorizer(static p => p.StartsWith("queue.", StringComparison.Ordinal));
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = true,
            Authorizer = authorizer,
        });

        using var abort = new CancellationTokenSource();
        using var response = await host.OpenStreamAsync(query, abort.Token).WaitAsync(HeadersBound);
        await abort.CancelAsync();

        using (new AssertionScope(query))
        {
            response.StatusCode.Should().Be(expected);
            host.Logs.Snapshot().Should().NotContain(static l => string.Equals(l.Category, SsePushLog.Category, StringComparison.Ordinal),
                "only a 403 is logged as a refusal");
        }
    }
}
