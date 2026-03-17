using DashboardExample.Models;
using DashboardExample.Services;
using DashboardExample.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DashboardExample.Tests.Services;

public class RecordingServiceTests
{
    private const string ServerId = "srv1";

    private static (RecordingService Sut, IRecordingPolicyRepository Repo) CreateService()
    {
        var repo = Substitute.For<IRecordingPolicyRepository>();
        var schema = Substitute.For<IRecordingMohSchemaManager>();
        var audioSvc = new AudioFileService(NullLogger<AudioFileService>.Instance);
        var logger = NullLogger<RecordingService>.Instance;
        // AsteriskMonitorService is sealed — pass null since live recording tests are out of scope
        var sut = new RecordingService(repo, schema, null!, audioSvc, logger);
        return (sut, repo);
    }

    private static RecordingPolicy ValidPolicy() => new()
    {
        ServerId = ServerId,
        Name = "default",
        Mode = RecordingMode.Always,
        Format = "wav",
        StoragePath = "/var/spool/asterisk/monitor/",
        RetentionDays = 90,
        Targets = [new PolicyTarget { TargetType = "extension", TargetValue = "1001" }]
    };

    [Fact]
    public async Task CreatePolicy_ShouldSucceed_WhenValid()
    {
        var (sut, repo) = CreateService();
        repo.GetByNameAsync(ServerId, "default", Arg.Any<CancellationToken>()).Returns((RecordingPolicy?)null);
        repo.InsertAsync(Arg.Any<RecordingPolicy>(), Arg.Any<CancellationToken>()).Returns(1);

        var (success, error) = await sut.CreatePolicyAsync(ServerId, ValidPolicy());

        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task CreatePolicy_ShouldFail_WhenNameDuplicate()
    {
        var (sut, repo) = CreateService();
        repo.GetByNameAsync(ServerId, "default", Arg.Any<CancellationToken>()).Returns(ValidPolicy());

        var (success, error) = await sut.CreatePolicyAsync(ServerId, ValidPolicy());

        success.Should().BeFalse();
        error.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreatePolicy_ShouldFail_WhenNoTargets()
    {
        var (sut, _) = CreateService();
        var policy = ValidPolicy();
        policy.Targets = [];

        var (success, error) = await sut.CreatePolicyAsync(ServerId, policy);

        success.Should().BeFalse();
        error.Should().Contain("target");
    }

    [Fact]
    public async Task CreatePolicy_ShouldFail_WhenEmptyName()
    {
        var (sut, _) = CreateService();
        var policy = ValidPolicy();
        policy.Name = "";

        var (success, error) = await sut.CreatePolicyAsync(ServerId, policy);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Theory]
    [InlineData("wav", true)]
    [InlineData("wav49", true)]
    [InlineData("gsm", true)]
    [InlineData("mp3", false)]
    [InlineData("ogg", false)]
    public async Task CreatePolicy_ShouldValidateFormat(string format, bool expectedValid)
    {
        var (sut, repo) = CreateService();
        repo.GetByNameAsync(ServerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((RecordingPolicy?)null);
        repo.InsertAsync(Arg.Any<RecordingPolicy>(), Arg.Any<CancellationToken>()).Returns(1);

        var policy = ValidPolicy();
        policy.Format = format;

        var (success, _) = await sut.CreatePolicyAsync(ServerId, policy);

        success.Should().Be(expectedValid);
    }

    [Fact]
    public async Task CreatePolicy_ShouldFail_WhenNegativeRetention()
    {
        var (sut, _) = CreateService();
        var policy = ValidPolicy();
        policy.RetentionDays = -1;

        var (success, error) = await sut.CreatePolicyAsync(ServerId, policy);

        success.Should().BeFalse();
        error.Should().Contain("Retention");
    }

    [Fact]
    public void GetActiveRecordings_ShouldTrackStartStop()
    {
        var (sut, _) = CreateService();

        sut.OnMixMonitorStarted("SIP/1001-00000001", "/var/spool/asterisk/monitor/test.wav");
        sut.GetActiveRecordings().Should().HaveCount(1);

        sut.OnMixMonitorStopped("SIP/1001-00000001");
        sut.GetActiveRecordings().Should().BeEmpty();
    }
}
