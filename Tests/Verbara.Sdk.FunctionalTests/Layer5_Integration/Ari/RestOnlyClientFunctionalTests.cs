namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Ari;

using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AriClient = Verbara.Sdk.Ari.Client.AriClient;

/// <summary>
/// A client built with <see cref="Verbara.Sdk.Ari.Client.AriClientOptions.EventsEnabled"/> off, run against a real Asterisk: it opens no event
/// WebSocket, so Asterisk registers no Stasis application for it, and its <c>ari</c> health check answers by pinging
/// Asterisk over REST.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it needs Asterisk.</b> Asterisk registers an application when an event WebSocket names it, and lists it under
/// <c>GET /applications</c>. Only a real Asterisk can say that a client which made REST calls and opened no socket left
/// that list as it was. The positive control shows the same list does gain an application when events are on, so an
/// absence is not a list that never shows anything.
/// </para>
/// <para>
/// <b>The witness.</b> A second client, with events on and its own application, reads the list. It waits until it sees
/// itself listed before the before-and-after reads, so neither read races its own registration, and it counts any
/// <c>ApplicationReplaced</c> it receives.
/// </para>
/// <para>
/// <b>Off the PR path</b>: this lane runs in the merge queue, on the scheduled matrix, and on a pull request only when it
/// carries the <c>ci:functional</c> label.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class RestOnlyClientFunctionalTests : FunctionalTestBase
{
    /// <summary>How long an events-on client has to appear under <c>GET /applications</c> after its connect returned.</summary>
    private static readonly TimeSpan ListingBudget = TimeSpan.FromSeconds(10);

    public RestOnlyClientFunctionalTests()
        : base("Verbara.Sdk.Ari")
    {
    }

    /// <summary>
    /// A REST-only client connects and makes a REST call, and the list of applications Asterisk holds gains nothing:
    /// its own application is absent, and the witness is told of no replacement.
    /// </summary>
    [Fact]
    public async Task RestOnlyClient_ShouldRegisterNoApplication_WhenItConnectsAndMakesRestCalls()
    {
        var witnessApp = "witness-" + Guid.NewGuid().ToString("N");
        var ghostApp = "ghost-" + Guid.NewGuid().ToString("N");

        await using var witness = AriClientFactory.Create(LoggerFactory, application: witnessApp);
        await witness.ConnectAsync();
        var replaced = 0;
        using var subscription = witness.Subscribe(new ReplacedCounter(() => Interlocked.Increment(ref replaced)));
        await WaitUntilListedAsync(witness, witnessApp);

        var before = await ApplicationNamesAsync(witness);

        await using var rest = AriClientFactory.Create(
            LoggerFactory, application: ghostApp, configure: o => o.EventsEnabled = false);
        await rest.ConnectAsync();
        var ping = await rest.Asterisk.PingAsync();

        var after = await ApplicationNamesAsync(witness);

        using (new AssertionScope())
        {
            ping.Should().NotBeNull("the REST call reached Asterisk and was answered");
            rest.State.Should().Be(AriConnectionState.RestOnly);
            after.Should().NotContain(ghostApp, "a client with events off opens no event WebSocket, so nothing registers its application");
            // Nothing added. Equality would also fail if an application from an earlier test went away between the
            // two reads, which says nothing about this client.
            after.Should().BeSubsetOf(before, "the REST-only client added no application to Asterisk's list");
            after.Should().Contain(witnessApp, "the witness is still connected, so the list is the live one");
            Volatile.Read(ref replaced).Should().Be(0, "no WebSocket took over the witness's application");
        }
    }

    /// <summary>
    /// The positive control: the same options with events on open the event WebSocket, and Asterisk lists the
    /// application. Without it, the absence above could be a list that never shows a client.
    /// </summary>
    [Fact]
    public async Task RestOnlyClient_ShouldBeListed_WhenEventsAreOn()
    {
        var app = "listed-" + Guid.NewGuid().ToString("N");

        await using var client = AriClientFactory.Create(
            LoggerFactory, application: app, configure: o => o.EventsEnabled = true);
        await client.ConnectAsync();

        client.State.Should().Be(AriConnectionState.Connected);
        await WaitUntilListedAsync(client, app);
    }

    /// <summary>
    /// The <c>ari</c> check of a REST-only client asks Asterisk, and a real Asterisk's answer is healthy.
    /// </summary>
    [Fact]
    public async Task AriHealthCheck_ShouldReportHealthy_WhenAsteriskAnswersTheRestOnlyPing()
    {
        await using var rest = AriClientFactory.Create(
            LoggerFactory, application: "health-" + Guid.NewGuid().ToString("N"), configure: o => o.EventsEnabled = false);
        await rest.ConnectAsync();

        var result = await new AriHealthCheck(rest).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Healthy, "Asterisk answered the ping");
            result.Data["ariState"].Should().Be("RestOnly");
            result.Exception.Should().BeNull();
        }
    }

    /// <summary>
    /// The <c>ari</c> check of a REST-only client with a wrong password: Asterisk refuses the ping, and the check is
    /// unhealthy with the exception on the result.
    /// </summary>
    [Fact]
    public async Task AriHealthCheck_ShouldReportUnhealthy_WhenThePasswordIsWrong()
    {
        const string wrongPassword = "wrong";

        await using var rest = AriClientFactory.Create(
            LoggerFactory,
            application: "health-" + Guid.NewGuid().ToString("N"),
            configure: o =>
            {
                o.EventsEnabled = false;
                o.Password = wrongPassword;
            });
        await rest.ConnectAsync();

        var result = await new AriHealthCheck(rest).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Unhealthy, "Asterisk refuses the credential");
            result.Data["ariState"].Should().Be("RestOnly");
            result.Exception.Should().NotBeNull("the refusal travels on the result");
            result.Description.Should().NotContain(wrongPassword, "a health report is read by dashboards");
        }
    }

    private static async Task<IReadOnlyCollection<string>> ApplicationNamesAsync(AriClient client)
    {
        var applications = await client.Applications.ListAsync();
        return applications.Select(a => a.Name).ToArray();
    }

    private static async Task WaitUntilListedAsync(AriClient client, string application)
    {
        var deadline = DateTime.UtcNow + ListingBudget;
        IReadOnlyCollection<string> names;
        do
        {
            names = await ApplicationNamesAsync(client);
            if (names.Contains(application))
                return;
            await Task.Delay(200); // fence-allow: LOOP-DRIVER — paces polling GET /applications, which Asterisk updates as the socket registers; ListingBudget bounds the loop
        }
        while (DateTime.UtcNow < deadline);

        names.Should().Contain(application,
            "an events-on client opened the event WebSocket for its application, so Asterisk must list it within the budget");
    }

    /// <summary>Counts every <see cref="ApplicationReplacedEvent"/> the observed client receives.</summary>
    private sealed class ReplacedCounter(Action onReplaced) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value)
        {
            if (value is ApplicationReplacedEvent)
                onReplaced();
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
