namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Net;
using FluentAssertions.Execution;

/// <summary>
/// The test host's identity modes work as the stream tests rely on them: a hand-built principal per request, and
/// real <c>JwtBearer</c> tokens the host mints, several identities sharing one host and one bus.
/// </summary>
public sealed class SseTestHostIdentityTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string Sentinel = "queue.9.sentinel";

    public static TheoryData<bool> Settings() => new() { false, true };

    public static TheoryData<bool, bool> TokenSettings() => new()
    {
        { true, false },
        { true, true },
        { false, false },
        { false, true },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task OpenStreamAsync_ShouldHandTheAuthorizerThePrincipal_WhenATestRegistersOneForTheRequest(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO, Authorizer = authorizer });
        var principal = SseTestHost.Principal(null, ("tenantId", "T7"), ("sub", "u7"));

        await using var stream = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, principal, ct), Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            stream.Status.Should().Be(HttpStatusCode.OK);
            authorizer.Subscribers.Should().ContainSingle("one pattern (the catch-all) was asked about");
            authorizer.Subscribers[0].TenantId.Should().Be("T7", "the registered principal, not the fixed one, is the subscriber");
            authorizer.Subscribers[0].UserId.Should().Be("u7");
        }
    }

    [Theory]
    [MemberData(nameof(TokenSettings))]
    public async Task OpenStreamWithTokenAsync_ShouldBeAdmitted_WhenTheHostMintedTheToken(bool mapInboundClaims, bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            Identity = SseIdentityMode.JwtBearer,
            MapInboundClaims = mapInboundClaims,
        });
        var claims = new Dictionary<string, object>(StringComparer.Ordinal) { ["tenantId"] = "T1", ["sub"] = "u1" };

        await using var stream = await SseStreamClient.OpenAsync(ct => host.OpenStreamWithTokenAsync(string.Empty, claims, ct), Bound);

        using (new AssertionScope($"MapInboundClaims={mapInboundClaims}, AllowSynchronousIO={allowSynchronousIO}"))
        {
            stream.Status.Should().Be(HttpStatusCode.OK, "the token is signed with the host's key for its issuer and audience");
            authorizer.Subscribers.Should().ContainSingle().Which.TenantId.Should().Be("T1", "the tenant comes from the validated token");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task OpenStreamWithTokenAsync_ShouldNotAuthenticate_WhenAnotherHostMintedTheToken(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            Identity = SseIdentityMode.JwtBearer,
        });
        await using var other = await SseTestHost.StartAsync(new SseHostOptions { Identity = SseIdentityMode.JwtBearer });
        var foreign = other.MintToken(new Dictionary<string, object>(StringComparer.Ordinal) { ["tenantId"] = "T1", ["sub"] = "u1" });

        await using var stream = await SseStreamClient.OpenAsync(
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(SseTestHost.StreamPath, UriKind.Relative));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", foreign);
                return await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            },
            Bound);
        var body = await stream.ReadBodyAsync(Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            stream.Status.Should().Be(HttpStatusCode.BadRequest, "a token signed with another key, issuer and audience authenticates no one");
            body.Should().Be("Missing tenantId claim.");
            authorizer.Asked.Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task OpenStreamAsync_ShouldKeepEachTenantsBroadcastOnItsOwnStream_WhenTwoTenantsShareOneHost(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        await using var t1 = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1")), ct), Bound);
        await using var t2 = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, SseTestHost.Principal(null, ("tenantId", "T2"), ("sub", "u2")), ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 2, Bound);

        await host.PublishAsync("queue.1.t1", tenant: "T1");
        await host.PublishAsync("queue.2.t2", tenant: "T2");
        await host.PublishAsync(Sentinel, tenant: "T1");
        await host.PublishAsync(Sentinel, tenant: "T2");

        var (sawT1, namesT1) = await t1.ReadUntilAsync(Sentinel, Bound);
        var (sawT2, namesT2) = await t2.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            t1.Status.Should().Be(HttpStatusCode.OK);
            t2.Status.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue("both streams subscribe to the one bus");
            sawT1.Should().BeTrue();
            sawT2.Should().BeTrue();
            namesT1.Should().Equal(["queue.1.t1"], "the T1 stream receives only T1's broadcast");
            namesT2.Should().Equal(["queue.2.t2"], "the T2 stream receives only T2's broadcast");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task OpenStreamWithTokenAsync_ShouldKeepEachUsersEventOnItsOwnStream_WhenTwoUsersShareOneHost(bool allowSynchronousIO)
    {
        // Inbound claim mapping off: the stream reads the user from the literal "sub" claim today.
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Identity = SseIdentityMode.JwtBearer,
            MapInboundClaims = false,
        });
        await using var u1 = await SseStreamClient.OpenAsync(
            ct => host.OpenStreamWithTokenAsync(string.Empty, new Dictionary<string, object>(StringComparer.Ordinal) { ["tenantId"] = "T1", ["sub"] = "u1" }, ct), Bound);
        await using var u2 = await SseStreamClient.OpenAsync(
            ct => host.OpenStreamWithTokenAsync(string.Empty, new Dictionary<string, object>(StringComparer.Ordinal) { ["tenantId"] = "T1", ["sub"] = "u2" }, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 2, Bound);

        await host.PublishAsync("agent.u1.state", user: "u1");
        await host.PublishAsync("agent.u2.state", user: "u2");
        await host.PublishAsync(Sentinel);

        var (sawU1, namesU1) = await u1.ReadUntilAsync(Sentinel, Bound);
        var (sawU2, namesU2) = await u2.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            u1.Status.Should().Be(HttpStatusCode.OK);
            u2.Status.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue("both streams subscribe to the one bus");
            sawU1.Should().BeTrue();
            sawU2.Should().BeTrue();
            namesU1.Should().Equal(["agent.u1.state"], "u1's stream receives only the event targeted at u1");
            namesU2.Should().Equal(["agent.u2.state"], "u2's stream receives only the event targeted at u2");
        }
    }
}
