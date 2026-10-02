namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// The four claim-type lists of <see cref="SsePushStreamOptions"/> on their own: how the subscriber is read from a
/// principal (precedence, union, the identity's own role type, case-insensitive types, whole ordinal values), the
/// defaults, configuration binding, and the start-time validation.
/// </summary>
public sealed class SseSubscriberIdentityFromTests
{
    [Fact]
    public void From_ShouldTakeTheFirstListedTypeWithAValue_WhenSeveralTenantTypesArePresent()
    {
        var options = new SsePushStreamOptions { TenantIdClaimTypes = ["tenant_id", "tid"] };
        var principal = Principal(ClaimTypes.Role, ("tid", "directory"), ("tenant_id", "T1"));

        SseSubscriberIdentity.From(principal, options).TenantId.Should().Be("T1", "list order is the precedence, not claim order");
    }

    [Fact]
    public void From_ShouldSkipATypeWhoseValueIsEmpty_WhenALaterTypeHasOne()
    {
        var principal = Principal(ClaimTypes.Role, ("tenantId", "T1"), ("sub", string.Empty), (ClaimTypes.NameIdentifier, "u1"));

        var identity = SseSubscriberIdentity.From(principal, new SsePushStreamOptions());

        identity.UserId.Should().Be("u1", "an empty sub yields no user, so the next listed type is read");
    }

    [Fact]
    public void From_ShouldReadTheUserFromNameIdentifier_WhenSubWasRenamed()
    {
        var principal = Principal(ClaimTypes.Role, ("tenantId", "T1"), (ClaimTypes.NameIdentifier, "u1"));

        SseSubscriberIdentity.From(principal, new SsePushStreamOptions()).UserId.Should().Be("u1");
    }

    [Fact]
    public void From_ShouldUniteEveryListedRoleTypeAndTheIdentitysRoleType_WhenRolesComeFromSeveralClaims()
    {
        var principal = Principal("groups",
            ("tenantId", "T1"), (ClaimTypes.Role, "a"), ("role", "b"), ("roles", "c"), ("groups", "d"), ("group", "e"));

        var identity = SseSubscriberIdentity.From(principal, new SsePushStreamOptions());

        identity.Roles.Should().BeEquivalentTo(["a", "b", "c", "d"], "the three default types plus the identity's own RoleClaimType; group is in no list");
    }

    [Fact]
    public void From_ShouldReadTheIdentitysRoleTypeOnly_WhenTheRoleListIsEmpty()
    {
        var options = new SsePushStreamOptions { RoleClaimTypes = [], PermissionClaimTypes = [] };
        var principal = Principal(ClaimTypes.Role, ("tenantId", "T1"), (ClaimTypes.Role, "supervisor"), ("role", "admin"), ("permission", "billing:read"));

        var identity = SseSubscriberIdentity.From(principal, options);

        identity.Roles.Should().BeEquivalentTo(["supervisor"], "the identity's RoleClaimType is always read; role is no longer listed");
        identity.Permissions.Should().BeEmpty();
    }

    [Fact]
    public void From_ShouldCompareClaimTypesIgnoringCase_WhenTheTokenCapitalisesThem()
    {
        var principal = Principal(ClaimTypes.Role, ("TenantId", "T1"), ("SUB", "u1"), ("Role", "supervisor"), ("PERMISSION", "billing:read"));

        var identity = SseSubscriberIdentity.From(principal, new SsePushStreamOptions());

        Describe(identity).Should().Be("tenant=T1 user=u1 roles=[supervisor] perms=[billing:read]");
    }

    [Fact]
    public void From_ShouldKeepValuesWholeAndOrdinal_WhenTheyDifferOnlyInCaseOrCarrySeparators()
    {
        var principal = Principal(ClaimTypes.Role,
            ("tenantId", "T1"), ("role", "Supervisor"), ("role", "supervisor"), ("permission", "billing:read queue:read"), ("permission", "a,b"));

        var identity = SseSubscriberIdentity.From(principal, new SsePushStreamOptions());

        identity.Roles.Should().BeEquivalentTo(["Supervisor", "supervisor"], "values are compared ordinally");
        identity.Permissions.Should().BeEquivalentTo(["billing:read queue:read", "a,b"], "a value is never split on spaces or commas");
    }

    [Fact]
    public void From_ShouldIgnoreEmptyValues_WhenAListedClaimIsEmpty()
    {
        var principal = Principal(ClaimTypes.Role, ("tenantId", string.Empty), ("role", string.Empty), ("permission", string.Empty));

        var identity = SseSubscriberIdentity.From(principal, new SsePushStreamOptions());

        Describe(identity).Should().Be("tenant=<null> user=<null> roles=[] perms=[]");
    }

    [Fact]
    public void From_ShouldReadAcrossEveryIdentity_WhenThePrincipalHasSeveral()
    {
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim("tenantId", "T1")], "first"),
            new ClaimsIdentity([new Claim("sub", "u1"), new Claim("permission", "billing:read")], "second"),
        ]);

        Describe(SseSubscriberIdentity.From(principal, new SsePushStreamOptions())).Should().Be("tenant=T1 user=u1 roles=[] perms=[billing:read]");
    }

    [Fact]
    public void From_ShouldNotThrow_WhenTheListsAreNullOrHoldBlankEntries()
    {
        var options = new SsePushStreamOptions
        {
            TenantIdClaimTypes = null!,
            UserIdClaimTypes = [null!, " ", "sub"],
            RoleClaimTypes = null!,
            PermissionClaimTypes = [string.Empty],
        };
        var principal = Principal(ClaimTypes.Role, ("tenantId", "T1"), ("sub", "u1"), (ClaimTypes.Role, "supervisor"), ("permission", "billing:read"));

        var identity = SseSubscriberIdentity.From(principal, options);

        Describe(identity).Should().Be("tenant=<null> user=u1 roles=[supervisor] perms=[]",
            "a null list or a blank entry contributes nothing; the identity's own role type is still read");
    }

    [Fact]
    public void Options_ShouldHaveTheDocumentedClaimTypeDefaults_WhenNothingIsConfigured()
    {
        var options = new SsePushStreamOptions();

        using (new AssertionScope())
        {
            options.TenantIdClaimTypes.Should().Equal(["tenantId"], "tid is deliberately not a default");
            options.UserIdClaimTypes.Should().Equal(["sub", ClaimTypes.NameIdentifier]);
            options.RoleClaimTypes.Should().Equal([ClaimTypes.Role, "role", "roles"]);
            options.PermissionClaimTypes.Should().Equal(["permission"]);
        }
    }

    [Fact]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test; measures the reflection-based configuration binder a host may use.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Test; measures the reflection-based configuration binder a host may use.")]
    public void Options_ShouldGetTheConfiguredEntriesAppendedToTheDefaults_WhenBoundFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Push:TenantIdClaimTypes:0"] = "tid",
                ["Push:PermissionClaimTypes:0"] = "permissions",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPushAspNetCore();
        services.Configure<SsePushStreamOptions>(configuration.GetSection("Push"));
        services.Configure<SsePushStreamOptions>(o => o.UserIdClaimTypes = ["uid"]);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<SsePushStreamOptions>>().Value;

        using (new AssertionScope())
        {
            options.TenantIdClaimTypes.Should().Equal(["tenantId", "tid"], "the configuration binder appends to an array that already has elements");
            options.PermissionClaimTypes.Should().Equal(["permission", "permissions"]);
            options.UserIdClaimTypes.Should().Equal(["uid"], "assigning the property in code replaces the default");
        }
    }

    [Theory]
    [InlineData(nameof(SsePushStreamOptions.TenantIdClaimTypes), "empty")]
    [InlineData(nameof(SsePushStreamOptions.UserIdClaimTypes), "empty")]
    [InlineData(nameof(SsePushStreamOptions.TenantIdClaimTypes), "null")]
    [InlineData(nameof(SsePushStreamOptions.UserIdClaimTypes), "blank")]
    [InlineData(nameof(SsePushStreamOptions.RoleClaimTypes), "blank")]
    [InlineData(nameof(SsePushStreamOptions.PermissionClaimTypes), "null")]
    [InlineData(nameof(SsePushStreamOptions.PermissionClaimTypes), "blank")]
    public void Options_ShouldFailValidationAtStartNamingTheList_WhenItIsUnusable(string property, string defect)
    {
        string[] value = defect switch
        {
            "empty" => [],
            "null" => null!,
            _ => ["sub", " "],
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPushAspNetCore();
        services.Configure<SsePushStreamOptions>(o =>
        {
            switch (property)
            {
                case nameof(SsePushStreamOptions.TenantIdClaimTypes): o.TenantIdClaimTypes = value; break;
                case nameof(SsePushStreamOptions.UserIdClaimTypes): o.UserIdClaimTypes = value; break;
                case nameof(SsePushStreamOptions.RoleClaimTypes): o.RoleClaimTypes = value; break;
                default: o.PermissionClaimTypes = value; break;
            }
        });
        using var sp = services.BuildServiceProvider();

        var start = () => sp.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(property);
    }

    [Fact]
    public void Options_ShouldPassValidation_WhenTheRoleAndPermissionListsAreEmpty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPushAspNetCore();
        services.Configure<SsePushStreamOptions>(o =>
        {
            o.RoleClaimTypes = [];
            o.PermissionClaimTypes = [];
        });
        using var sp = services.BuildServiceProvider();

        var start = () => sp.GetRequiredService<IStartupValidator>().Validate();

        start.Should().NotThrow("the role and permission lists may be empty");
    }

    internal static ClaimsPrincipal Principal(string roleType, params (string Type, string Value)[] claims) =>
        SseTestHost.Principal(roleType, claims);

    internal static string Describe(SseSubscriberIdentity s) =>
        $"tenant={s.TenantId ?? "<null>"} user={s.UserId ?? "<null>"} roles=[{string.Join(",", s.Roles.Order(StringComparer.Ordinal))}] perms=[{string.Join(",", s.Permissions.Order(StringComparer.Ordinal))}]";
}

/// <summary>
/// The claim-type lists on a real host: a tenant claim the host names, a permission type the host adds, empty role
/// and permission lists, and an unvalidated host whose tenant list is empty.
/// </summary>
public sealed class SseSubscriberClaimTypesHostTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string Sentinel = "queue.9.sentinel";

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldServeTheTenantFromTheHostsClaimType_WhenTheTenantListNamesIt(bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            ConfigureStream = static o => o.TenantIdClaimTypes = ["org"],
        });
        var principal = SseTestHost.Principal(null, ("org", "T1"), ("sub", "u1"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, principal, ct), Bound);
        var subscribed = await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishAsync("queue.a.updated");
        await host.PublishAsync(Sentinel);
        var (saw, names) = await client.ReadUntilAsync(Sentinel, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK, "org is the host's tenant claim type");
            subscribed.Should().BeTrue();
            saw.Should().BeTrue();
            names.Should().Equal(["queue.a.updated"], "the T1 broadcast reaches the T1 subscriber found through org");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task StreamRequest_ShouldHandTheAuthorizerTheAddedPermissionType_WhenTheHostListsScp(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static (s, pattern) => pattern == "billing.**" && s.HasPermission("billing:read"));
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            ConfigureStream = static o => o.PermissionClaimTypes = ["permission", "scp"],
        });
        var principal = SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1"), ("scp", "billing:read"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync("?topic=billing.**", principal, ct), Bound);
        var handed = authorizer.Subscribers;

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK, "scp was added to the permission claim types");
            handed.Should().ContainSingle();
            handed[0].Permissions.Should().BeEquivalentTo(["billing:read"]);
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task StreamRequest_ShouldReadOnlyTheIdentitysRoleType_WhenTheRoleAndPermissionListsAreEmpty(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            ConfigureStream = static o =>
            {
                o.RoleClaimTypes = [];
                o.PermissionClaimTypes = [];
            },
        });
        var principal = SseTestHost.Principal(ClaimTypes.Role,
            ("tenantId", "T1"), ("sub", "u1"), (ClaimTypes.Role, "supervisor"), ("role", "admin"), ("permission", "billing:read"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, principal, ct), Bound);
        var handed = authorizer.Subscribers;

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.OK, "the host starts with empty role and permission lists");
            handed.Should().ContainSingle();
            handed[0].Roles.Should().BeEquivalentTo(["supervisor"], "only the identity's own RoleClaimType is read");
            handed[0].Permissions.Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task StreamRequest_ShouldBeAnsweredMissingTenant_WhenAnUnvalidatedHostEmptiedTheTenantList(bool allowSynchronousIO)
    {
        var authorizer = new RecordingAuthorizer(static _ => true);
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Authorizer = authorizer,
            Registration = PushRegistration.PushOnly,
            ConfigureStream = static o => o.TenantIdClaimTypes = [],
        });
        var principal = SseTestHost.Principal(null, ("tenantId", "T1"), ("sub", "u1"));

        await using var client = await SseStreamClient.OpenAsync(ct => host.OpenStreamAsync(string.Empty, principal, ct), Bound);
        var body = await client.ReadBodyAsync(Bound);
        var completed = await host.StreamRequestsCompleted.WaitUntilAsync(static n => n >= 1, Bound);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            client.Status.Should().Be(HttpStatusCode.BadRequest, "no claim type is left to read the tenant from");
            body.Should().Be("Missing tenantId claim.");
            completed.Should().BeTrue();
            authorizer.Asked.Should().BeEmpty();
            host.LoggedExceptions.Select(static l => l.Exception!.GetType().Name + ": " + l.Exception.Message)
                .Should().BeEmpty("an unusable list is never a server error");
        }
    }
}
