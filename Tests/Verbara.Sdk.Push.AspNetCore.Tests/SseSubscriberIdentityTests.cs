namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Net;
using System.Security.Claims;
using FluentAssertions.Execution;

/// <summary>
/// Who a stream connection belongs to — tenant, user, roles and permissions — is read from the authenticated
/// principal through the default claim types: with real <c>JwtBearer</c> tokens (inbound claim mapping on and off)
/// and with hand-built principals. The authorizer and the delivery filter are handed the same subscriber.
/// </summary>
public sealed class SseSubscriberIdentityTests
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

    /// <summary>The claim carrying the role or permission, the query it asks, and the setting.</summary>
    public static TheoryData<string, string, string, bool> HandBuiltGrants()
    {
        var data = new TheoryData<string, string, string, bool>();
        foreach (var allowSynchronousIO in new[] { false, true })
        {
            data.Add(ClaimTypes.Role, "supervisor", string.Empty, allowSynchronousIO);
            data.Add("role", "supervisor", string.Empty, allowSynchronousIO);
            data.Add("roles", "supervisor", string.Empty, allowSynchronousIO);
            data.Add("permission", "billing:read", "?topic=billing.**", allowSynchronousIO);
        }

        return data;
    }

    /// <summary>Allows <c>**</c> only to the role <c>supervisor</c> and <c>billing.**</c> only to the permission <c>billing:read</c>.</summary>
    private static bool SupervisorOrBillingReader(SubscriberContext subscriber, string pattern) => pattern switch
    {
        "**" => subscriber.HasRole("supervisor"),
        "billing.**" => subscriber.HasPermission("billing:read"),
        _ => false,
    };

    private static Dictionary<string, object> TokenClaims() => new(StringComparer.Ordinal)
    {
        ["sub"] = "u1",
        ["tenantId"] = "T1",
        ["role"] = "supervisor",
        ["permission"] = "billing:read",
    };

    [Theory]
    [MemberData(nameof(TokenSettings))]
    public async Task Stream_ShouldDeliverTheUsersEvents_WhenJwtBearerAuthenticatesTheToken(bool mapInboundClaims, bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Identity = SseIdentityMode.JwtBearer,
            MapInboundClaims = mapInboundClaims,
        });
        await using var client = await SseStreamClient.OpenAsync(
            ct => host.OpenStreamWithTokenAsync("?topic=agent.{self}.**&topic=queue.**", TokenClaims(), ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishAsync("agent.u1.state");
        await host.PublishAsync("queue.a.x", user: "u1");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"MapInboundClaims={mapInboundClaims}, AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue();
            saw.Should().BeTrue("the sentinel is a queue.** broadcast");
            names.Should().Equal(["agent.u1.state", "queue.a.x"],
                "the user u1 is known whether or not the handler renamed sub, so {self} resolves and the u1-targeted event arrives");
        }
    }

    [Theory]
    [MemberData(nameof(TokenSettings))]
    public async Task StreamRequest_ShouldHandTheAuthorizerTheTokensUserRolesAndPermissions_WhenJwtBearerAuthenticatesIt(bool mapInboundClaims, bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(SupervisorOrBillingReader);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            Identity = SseIdentityMode.JwtBearer,
            MapInboundClaims = mapInboundClaims,
        });
        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamWithTokenAsync("?topic=billing.**", TokenClaims(), ct), Bound);
        var handed = authorizer.Subscribers;

        using (new AssertionScope($"MapInboundClaims={mapInboundClaims}, AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK, "the token carries the permission billing:read");
            handed.Should().ContainSingle();
            Describe(handed[0]).Should().Be("tenant=T1 user=u1 roles=[supervisor] perms=[billing:read]",
                "the authorizer is handed the token's user, role and permission");
        }
    }

    [Theory]
    [MemberData(nameof(HandBuiltGrants))]
    public async Task StreamRequest_ShouldBeAdmitted_WhenTheGrantIsCarriedByADefaultClaimType(string claimType, string value, string query, bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(SupervisorOrBillingReader);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO, Authorizer = authorizer });
        var principal = SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1"), (claimType, value));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(query, principal, ct), Bound);
        var handed = authorizer.Subscribers;
        var isPermission = string.Equals(claimType, "permission", StringComparison.Ordinal);

        using (new AssertionScope($"{claimType}={value} {query}, AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK, "{0} is a default {1} claim type", claimType, isPermission ? "permission" : "role");
            handed.Should().ContainSingle();
            Describe(handed[0]).Should().Be(isPermission
                ? "tenant=T1 user=u1 roles=[] perms=[billing:read]"
                : "tenant=T1 user=u1 roles=[supervisor] perms=[]");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task StreamRequest_ShouldBeDeniedWithNoRoleOrPermission_WhenTheClaimsAreOutsideTheDefaultTypes(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(SupervisorOrBillingReader);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO, Authorizer = authorizer });
        var principal = SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1"), ("scp", "billing:read"), ("group", "supervisor"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync("?topic=billing.**", principal, ct), Bound);
        var handed = authorizer.Subscribers;

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.Forbidden);
            handed.Should().ContainSingle();
            Describe(handed[0]).Should().Be("tenant=T1 user=u1 roles=[] perms=[]", "scp and group are in no default list");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task StreamRequest_ShouldBeRefusedBeforeAskingOrSubscribing_WhenOnlyTidCarriesTheTenant(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO, Authorizer = authorizer });
        var principal = SseTestHost.Principal(null, ("tid", "T1"), ("sub", "u1"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, principal, ct), Bound);
        var body = await client.ReadBodyAsync(Bound);
        var completed = await host.StreamRequestsCompleted.WaitUntilAsync(static n => n >= 1, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.BadRequest);
            body.Should().Be("Missing tenantId claim.");
            completed.Should().BeTrue();
            authorizer.Asked.Should().BeEmpty("tid is not a default tenant claim type, so nothing is asked");
            host.Bus.Subscribers.Value.Should().Be(0, "nothing subscribed to the bus");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldFindTheTenantAndTheUser_WhenTheirClaimTypesDifferOnlyInCase(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });
        var principal = SseTestHost.Principal(null, ("TenantId", "T1"), ("Sub", "u1"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync("?topic=agent.{self}.**&topic=" + Sentinel, principal, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishAsync("agent.u1.state");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK, "claim types are compared ignoring case");
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["agent.u1.state"], "the user u1 is found through Sub, so {self} resolves");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldHandTheDeliveryFilterTheSubscriberTheAuthorizerSaw_WhenAnEventIsEvaluated(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        var filter = new RecordingDeliveryFilter();
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            DeliveryFilter = filter,
        });
        var principal = SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1"), ("role", "supervisor"), ("permission", "billing:read"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync("?topic=billing.**&topic=" + Sentinel, principal, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishAsync("billing.invoice.created");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);
        var seen = filter.Seen;
        var handed = authorizer.Subscribers;

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["billing.invoice.created"]);
            seen.Should().NotBeEmpty("the delivery filter evaluated the published events");
            handed.Should().NotBeEmpty();
            seen.Select(Describe).Should().AllBe("tenant=T1 user=u1 roles=[supervisor] perms=[billing:read]",
                "the delivery filter is handed the subscriber's tenant, user, roles and permissions");
            seen.Select(Describe).Should().AllBe(Describe(handed[0]), "the delivery filter sees the subscriber the authorizer saw");
        }
    }

    /// <summary>A subscriber as one comparable line: tenant, user, and the sorted roles and permissions.</summary>
    private static string Describe(SubscriberContext s) =>
        $"tenant={s.TenantId} user={s.UserId ?? "<null>"} roles=[{string.Join(",", s.Roles.Order(StringComparer.Ordinal))}] perms=[{string.Join(",", s.Permissions.Order(StringComparer.Ordinal))}]";
}
