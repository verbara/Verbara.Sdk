using DashboardExample.Models;
using DashboardExample.Services;
using DashboardExample.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DashboardExample.Tests.Services;

public class QueueConfigServiceTests
{
    [Fact]
    public async Task CreateQueue_ShouldReject_WhenNameEmpty()
    {
        var sut = CreateService();
        var config = ValidQueue() with { Name = "" };

        var (success, error) = await sut.CreateQueueAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("name is required");
    }

    [Fact]
    public async Task CreateQueue_ShouldReject_WhenNameHasInvalidChars()
    {
        var sut = CreateService();
        var config = ValidQueue() with { Name = "my queue!" };

        var (success, error) = await sut.CreateQueueAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("letters, numbers, hyphens");
    }

    [Theory]
    [InlineData("ringall", true)]
    [InlineData("roundrobin", true)]
    [InlineData("leastrecent", true)]
    [InlineData("fewestcalls", true)]
    [InlineData("random", true)]
    [InlineData("rrmemory", true)]
    [InlineData("linear", true)]
    [InlineData("wrandom", true)]
    [InlineData("invalid", false)]
    [InlineData("", false)]
    public void ValidateQueue_ShouldCheckStrategy(string strategy, bool shouldBeValid)
    {
        var config = ValidQueue() with { Strategy = strategy };
        var error = QueueConfigService.ValidateQueue(config);

        if (shouldBeValid)
            error.Should().BeNull();
        else
            error.Should().Contain("strategy");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(15, true)]
    public void ValidateQueue_ShouldCheckTimeout(int timeout, bool shouldBeValid)
    {
        var config = ValidQueue() with { Timeout = timeout };
        var error = QueueConfigService.ValidateQueue(config);

        if (shouldBeValid)
            error.Should().BeNull();
        else
            error.Should().Contain("Timeout");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    public void ValidateQueue_ShouldCheckRetry(int retry, bool shouldBeValid)
    {
        var config = ValidQueue() with { Retry = retry };
        var error = QueueConfigService.ValidateQueue(config);

        if (shouldBeValid)
            error.Should().BeNull();
        else
            error.Should().Contain("Retry");
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", true)]
    [InlineData("strict", true)]
    [InlineData("loose", true)]
    [InlineData("invalid", false)]
    public void ValidateQueue_ShouldCheckJoinEmpty(string value, bool shouldBeValid)
    {
        var config = ValidQueue() with { JoinEmpty = value };
        var error = QueueConfigService.ValidateQueue(config);

        if (shouldBeValid)
            error.Should().BeNull();
        else
            error.Should().Contain("joinempty");
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", true)]
    [InlineData("once", true)]
    [InlineData("invalid", false)]
    public void ValidateQueue_ShouldCheckAnnounceHoldTime(string value, bool shouldBeValid)
    {
        var config = ValidQueue() with { AnnounceHoldTime = value };
        var error = QueueConfigService.ValidateQueue(config);

        if (shouldBeValid)
            error.Should().BeNull();
        else
            error.Should().Contain("announce_holdtime");
    }

    [Fact]
    public void ValidateMember_ShouldReject_WhenInterfaceEmpty()
    {
        var member = new QueueMemberConfigDto { Interface = "" };
        var error = QueueConfigService.ValidateMember(member);

        error.Should().Contain("interface is required");
    }

    [Theory]
    [InlineData("PJSIP/2001", true)]
    [InlineData("SIP/trunk1", true)]
    [InlineData("Local/2001@from-queue/n", true)]
    [InlineData("2001", false)]
    public void ValidateMember_ShouldCheckInterfaceFormat(string iface, bool shouldBeValid)
    {
        var member = new QueueMemberConfigDto { Interface = iface, Penalty = 0 };
        var error = QueueConfigService.ValidateMember(member);

        if (shouldBeValid)
            error.Should().BeNull();
        else
            error.Should().NotBeNull();
    }

    [Fact]
    public void ValidateMember_ShouldReject_WhenPenaltyNegative()
    {
        var member = new QueueMemberConfigDto { Interface = "PJSIP/2001", Penalty = -1 };
        var error = QueueConfigService.ValidateMember(member);

        error.Should().Contain("Penalty");
    }

    [Fact]
    public void ValidateMember_ShouldReject_WhenMemberNameTooLong()
    {
        var member = new QueueMemberConfigDto { Interface = "PJSIP/2001", MemberName = new string('A', 129) };
        var error = QueueConfigService.ValidateMember(member);

        error.Should().Contain("128 characters");
    }

    [Fact]
    public async Task CreateQueue_ShouldReject_WhenNameAlreadyExists()
    {
        var repo = Substitute.For<IQueueConfigRepository>();
        repo.GetQueueByNameAsync("s1", "sales", Arg.Any<CancellationToken>())
            .Returns(new QueueConfigDto { Id = 99, ServerId = "s1", Name = "sales" });

        var sut = CreateService(repo);
        var config = ValidQueue();

        var (success, error) = await sut.CreateQueueAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateQueue_ShouldReject_WhenDuplicateMemberInterface()
    {
        var sut = CreateService();
        var config = ValidQueue();
        config.Members =
        [
            new QueueMemberConfigDto { Interface = "PJSIP/2001" },
            new QueueMemberConfigDto { Interface = "PJSIP/2001" },
        ];

        var (success, error) = await sut.CreateQueueAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("Duplicate member interface");
    }

    [Fact]
    public async Task CreateQueue_ShouldReload_AfterSuccess()
    {
        var repo = Substitute.For<IQueueConfigRepository>();
        repo.GetQueueByNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((QueueConfigDto?)null);
        repo.CreateQueueAsync(Arg.Any<QueueConfigDto>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var provider = Substitute.For<IConfigProvider>();
        var providerResolver = Substitute.For<IConfigProviderResolver>();
        providerResolver.GetProvider(Arg.Any<string>()).Returns(provider);

        var viewManager = Substitute.For<IQueueViewManager>();

        var sut = new QueueConfigService(repo, viewManager, providerResolver,
            Substitute.For<ILogger<QueueConfigService>>());

        var config = ValidQueue();
        var (success, _) = await sut.CreateQueueAsync(config);

        success.Should().BeTrue();
        await provider.Received(1).ReloadModuleAsync("s1", "app_queue.so", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMember_ShouldReload_AfterSuccess()
    {
        var repo = Substitute.For<IQueueConfigRepository>();
        repo.AddMemberAsync(Arg.Any<QueueMemberConfigDto>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var provider = Substitute.For<IConfigProvider>();
        var providerResolver = Substitute.For<IConfigProviderResolver>();
        providerResolver.GetProvider(Arg.Any<string>()).Returns(provider);

        var viewManager = Substitute.For<IQueueViewManager>();

        var sut = new QueueConfigService(repo, viewManager, providerResolver,
            Substitute.For<ILogger<QueueConfigService>>());

        var member = new QueueMemberConfigDto { QueueConfigId = 1, Interface = "PJSIP/2001" };
        var (success, _) = await sut.AddMemberAsync("s1", "sales", member);

        success.Should().BeTrue();
        await provider.Received(1).ReloadModuleAsync("s1", "app_queue.so", Arg.Any<CancellationToken>());
    }

    private static QueueConfigDto ValidQueue() => new()
    {
        ServerId = "s1",
        Name = "sales",
        Strategy = "ringall",
        Timeout = 15,
        Retry = 5,
        MaxLen = 0,
        WrapUpTime = 0,
        ServiceLevel = 60,
        MusicOnHold = "default",
        JoinEmpty = "yes",
        LeaveWhenEmpty = "no",
        RingInUse = "no",
        AnnounceHoldTime = "no",
        AnnouncePosition = "no",
    };

    private static QueueConfigService CreateService(IQueueConfigRepository? repo = null)
    {
        repo ??= Substitute.For<IQueueConfigRepository>();
        var viewManager = Substitute.For<IQueueViewManager>();
        var providerResolver = Substitute.For<IConfigProviderResolver>();
        var logger = Substitute.For<ILogger<QueueConfigService>>();
        return new QueueConfigService(repo, viewManager, providerResolver, logger);
    }
}
