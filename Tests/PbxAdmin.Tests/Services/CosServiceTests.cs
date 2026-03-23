using System.Globalization;
using PbxAdmin.Models;
using PbxAdmin.Services;
using PbxAdmin.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace PbxAdmin.Tests.Services;

public class CosServiceTests
{
    // -----------------------------------------------------------------------
    // CRUD: CreateLevelAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateLevelAsync_ShouldReject_WhenNameEmpty()
    {
        var sut = CreateService(out _, out _);
        var level = new CosLevel { ServerId = "s1", Name = "", AsteriskContext = "cos-test" };

        var (success, error) = await sut.CreateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task CreateLevelAsync_ShouldReject_WhenNameWhitespace()
    {
        var sut = CreateService(out _, out _);
        var level = new CosLevel { ServerId = "s1", Name = "   ", AsteriskContext = "cos-test" };

        var (success, error) = await sut.CreateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task CreateLevelAsync_ShouldReject_WhenContextEmpty()
    {
        var sut = CreateService(out _, out _);
        var level = new CosLevel { ServerId = "s1", Name = "Standard", AsteriskContext = "" };

        var (success, error) = await sut.CreateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("context");
    }

    [Fact]
    public async Task CreateLevelAsync_ShouldReject_WhenDuplicateName()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelsAsync("s1", Arg.Any<CancellationToken>())
            .Returns([new CosLevel { Id = 1, ServerId = "s1", Name = "Standard", AsteriskContext = "cos-std" }]);

        var level = new CosLevel { ServerId = "s1", Name = "Standard", AsteriskContext = "cos-new" };

        var (success, error) = await sut.CreateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("exists");
    }

    [Fact]
    public async Task CreateLevelAsync_ShouldSucceed_WhenValid()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelsAsync("s1", Arg.Any<CancellationToken>()).Returns([]);
        repo.CreateLevelAsync(Arg.Any<CosLevel>(), Arg.Any<CancellationToken>()).Returns(42);

        var level = new CosLevel { ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" };

        var (success, error) = await sut.CreateLevelAsync(level);

        success.Should().BeTrue();
        error.Should().BeNull();
        await repo.Received(1).CreateLevelAsync(level, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLevelAsync_ShouldWriteAuditLog()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelsAsync("s1", Arg.Any<CancellationToken>()).Returns([]);
        repo.CreateLevelAsync(Arg.Any<CosLevel>(), Arg.Any<CancellationToken>()).Returns(1);

        var level = new CosLevel { ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" };
        await sut.CreateLevelAsync(level);

        await repo.Received(1).AppendAuditLogAsync(
            Arg.Is<CosAuditEntry>(e =>
                e.ServerId == "s1" &&
                e.EntityType == "level" &&
                e.Action == "created" &&
                e.NewValue == "Premium"),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // CRUD: UpdateLevelAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateLevelAsync_ShouldReject_WhenNameEmpty()
    {
        var sut = CreateService(out _, out _);
        var level = new CosLevel { Id = 1, ServerId = "s1", Name = "", AsteriskContext = "cos-test" };

        var (success, error) = await sut.UpdateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task UpdateLevelAsync_ShouldReject_WhenContextEmpty()
    {
        var sut = CreateService(out _, out _);
        var level = new CosLevel { Id = 1, ServerId = "s1", Name = "Standard", AsteriskContext = "" };

        var (success, error) = await sut.UpdateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("context");
    }

    [Fact]
    public async Task UpdateLevelAsync_ShouldSucceed_WhenValid()
    {
        var sut = CreateService(out var repo, out _);
        repo.UpdateLevelAsync(Arg.Any<CosLevel>(), Arg.Any<CancellationToken>()).Returns(true);

        var level = new CosLevel { Id = 1, ServerId = "s1", Name = "Standard", AsteriskContext = "cos-std" };

        var (success, error) = await sut.UpdateLevelAsync(level);

        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task UpdateLevelAsync_ShouldFail_WhenNotFound()
    {
        var sut = CreateService(out var repo, out _);
        repo.UpdateLevelAsync(Arg.Any<CosLevel>(), Arg.Any<CancellationToken>()).Returns(false);

        var level = new CosLevel { Id = 99, ServerId = "s1", Name = "Standard", AsteriskContext = "cos-std" };

        var (success, error) = await sut.UpdateLevelAsync(level);

        success.Should().BeFalse();
        error.Should().Contain("not found");
    }

    [Fact]
    public async Task UpdateLevelAsync_ShouldWriteAuditLog()
    {
        var sut = CreateService(out var repo, out _);
        repo.UpdateLevelAsync(Arg.Any<CosLevel>(), Arg.Any<CancellationToken>()).Returns(true);

        var level = new CosLevel { Id = 1, ServerId = "s1", Name = "Standard", AsteriskContext = "cos-std" };
        await sut.UpdateLevelAsync(level);

        await repo.Received(1).AppendAuditLogAsync(
            Arg.Is<CosAuditEntry>(e =>
                e.ServerId == "s1" &&
                e.EntityType == "level" &&
                e.Action == "updated"),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // CRUD: DeleteLevelAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteLevelAsync_ShouldReject_WhenExtensionsAssigned()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetExtensionOverridesAsync("s1", Arg.Any<CancellationToken>())
            .Returns([new CosExtensionOverride { ServerId = "s1", Extension = "2001", CosLevelId = 5 }]);

        var (success, error) = await sut.DeleteLevelAsync(5, "s1");

        success.Should().BeFalse();
        error.Should().Contain("assigned");
    }

    [Fact]
    public async Task DeleteLevelAsync_ShouldSucceed_WhenNoExtensionsAssigned()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetExtensionOverridesAsync("s1", Arg.Any<CancellationToken>()).Returns([]);
        repo.DeleteLevelAsync(5, Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteLevelAsync(5, "s1");

        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task DeleteLevelAsync_ShouldFail_WhenNotFound()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetExtensionOverridesAsync("s1", Arg.Any<CancellationToken>()).Returns([]);
        repo.DeleteLevelAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var (success, error) = await sut.DeleteLevelAsync(99, "s1");

        success.Should().BeFalse();
        error.Should().Contain("not found");
    }

    [Fact]
    public async Task DeleteLevelAsync_ShouldWriteAuditLog()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetExtensionOverridesAsync("s1", Arg.Any<CancellationToken>()).Returns([]);
        repo.DeleteLevelAsync(5, Arg.Any<CancellationToken>()).Returns(true);

        await sut.DeleteLevelAsync(5, "s1");

        await repo.Received(1).AppendAuditLogAsync(
            Arg.Is<CosAuditEntry>(e =>
                e.ServerId == "s1" &&
                e.EntityType == "level" &&
                e.Action == "deleted"),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Assignment: AssignToExtensionAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AssignToExtensionAsync_ShouldFail_WhenLevelNotFound()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelAsync(99, Arg.Any<CancellationToken>()).Returns((CosLevel?)null);

        var (success, error) = await sut.AssignToExtensionAsync("s1", "2001", 99);

        success.Should().BeFalse();
        error.Should().Contain("not found");
    }

    [Fact]
    public async Task AssignToExtensionAsync_ShouldSucceed_WhenLevelExists()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelAsync(5, Arg.Any<CancellationToken>())
            .Returns(new CosLevel { Id = 5, ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" });

        var (success, error) = await sut.AssignToExtensionAsync("s1", "2001", 5);

        success.Should().BeTrue();
        error.Should().BeNull();
        await repo.Received(1).UpsertExtensionOverrideAsync(
            Arg.Is<CosExtensionOverride>(o =>
                o.ServerId == "s1" &&
                o.Extension == "2001" &&
                o.CosLevelId == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignToExtensionAsync_ShouldWriteAuditLog()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelAsync(5, Arg.Any<CancellationToken>())
            .Returns(new CosLevel { Id = 5, ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" });

        await sut.AssignToExtensionAsync("s1", "2001", 5);

        await repo.Received(1).AppendAuditLogAsync(
            Arg.Is<CosAuditEntry>(e =>
                e.ServerId == "s1" &&
                e.EntityType == "extension" &&
                e.EntityId == "2001" &&
                e.Action == "assigned" &&
                e.NewValue == "Premium"),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // BulkAssignAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BulkAssignAsync_ShouldFail_WhenLevelNotFound()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelAsync(99, Arg.Any<CancellationToken>()).Returns((CosLevel?)null);

        var (succeeded, failed, errors) = await sut.BulkAssignAsync("s1", ["2001", "2002"], 99);

        succeeded.Should().Be(0);
        failed.Should().Be(0);
        errors.Should().ContainSingle().Which.Should().Contain("not found");
    }

    [Fact]
    public async Task BulkAssignAsync_ShouldProcessAllExtensions()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelAsync(5, Arg.Any<CancellationToken>())
            .Returns(new CosLevel { Id = 5, ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" });
        repo.UpsertExtensionOverrideAsync(Arg.Any<CosExtensionOverride>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var (succeeded, failed, errors) = await sut.BulkAssignAsync("s1", ["2001", "2002", "2003"], 5);

        succeeded.Should().Be(3);
        failed.Should().Be(0);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkAssignAsync_ShouldReportProgress()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetLevelAsync(5, Arg.Any<CancellationToken>())
            .Returns(new CosLevel { Id = 5, ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" });
        repo.UpsertExtensionOverrideAsync(Arg.Any<CosExtensionOverride>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var progressValues = new List<int>();
        await sut.BulkAssignAsync("s1", ["2001", "2002"], 5, progressValues.Add);

        progressValues.Should().BeEquivalentTo([1, 2]);
    }

    // -----------------------------------------------------------------------
    // GetExtensionCosAsync (override vs default hierarchy)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetExtensionCosAsync_ShouldUseOverride_WhenExtensionHasOverride()
    {
        var sut = CreateService(out var repo, out _);
        var level = new CosLevel { Id = 5, Name = "Custom", AsteriskContext = "cos-custom" };
        repo.GetExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>())
            .Returns(new CosExtensionOverride { Extension = "2001", CosLevelId = 5 });
        repo.GetLevelAsync(5, Arg.Any<CancellationToken>()).Returns(level);

        var result = await sut.GetExtensionCosAsync("s1", "2001");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Custom");
    }

    [Fact]
    public async Task GetExtensionCosAsync_ShouldUseDefault_WhenNoOverride()
    {
        var config = BuildConfig(defaultCosLevelId: 10);
        var sut = CreateService(out var repo, out _, config);
        var defaultLevel = new CosLevel { Id = 10, Name = "Default", AsteriskContext = "cos-default" };
        repo.GetExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>())
            .Returns((CosExtensionOverride?)null);
        repo.GetLevelAsync(10, Arg.Any<CancellationToken>()).Returns(defaultLevel);

        var result = await sut.GetExtensionCosAsync("s1", "2001");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Default");
    }

    [Fact]
    public async Task GetExtensionCosAsync_ShouldReturnNull_WhenNoOverrideAndNoDefault()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>())
            .Returns((CosExtensionOverride?)null);

        var result = await sut.GetExtensionCosAsync("s1", "2001");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetExtensionCosAsync_ShouldReturnNull_WhenOverrideHasNullCosLevelId()
    {
        var sut = CreateService(out var repo, out _);
        repo.GetExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>())
            .Returns(new CosExtensionOverride { Extension = "2001", CosLevelId = null });

        var result = await sut.GetExtensionCosAsync("s1", "2001");

        result.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // GetLevelsAsync / GetLevelAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetLevelsAsync_ShouldReturnFromRepo()
    {
        var sut = CreateService(out var repo, out _);
        var expected = new List<CosLevel>
        {
            new() { Id = 1, ServerId = "s1", Name = "Basic", AsteriskContext = "cos-basic" },
            new() { Id = 2, ServerId = "s1", Name = "Premium", AsteriskContext = "cos-premium" },
        };
        repo.GetLevelsAsync("s1", Arg.Any<CancellationToken>()).Returns(expected);

        var result = await sut.GetLevelsAsync("s1");

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetLevelAsync_ShouldReturnFromRepo()
    {
        var sut = CreateService(out var repo, out _);
        var level = new CosLevel { Id = 1, ServerId = "s1", Name = "Basic", AsteriskContext = "cos-basic" };
        repo.GetLevelAsync(1, Arg.Any<CancellationToken>()).Returns(level);

        var result = await sut.GetLevelAsync(1, "s1");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Basic");
    }

    // -----------------------------------------------------------------------
    // Extension overrides
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetExtensionOverridesAsync_ShouldReturnFromRepo()
    {
        var sut = CreateService(out var repo, out _);
        var overrides = new List<CosExtensionOverride>
        {
            new() { ServerId = "s1", Extension = "2001", CosLevelId = 1 },
        };
        repo.GetExtensionOverridesAsync("s1", Arg.Any<CancellationToken>()).Returns(overrides);

        var result = await sut.GetExtensionOverridesAsync("s1");

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteExtensionOverrideAsync_ShouldSucceed_WhenExists()
    {
        var sut = CreateService(out var repo, out _);
        repo.DeleteExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteExtensionOverrideAsync("s1", "2001");

        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task DeleteExtensionOverrideAsync_ShouldFail_WhenNotFound()
    {
        var sut = CreateService(out var repo, out _);
        repo.DeleteExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>()).Returns(false);

        var (success, error) = await sut.DeleteExtensionOverrideAsync("s1", "2001");

        success.Should().BeFalse();
        error.Should().Contain("not found");
    }

    [Fact]
    public async Task DeleteExtensionOverrideAsync_ShouldWriteAuditLog()
    {
        var sut = CreateService(out var repo, out _);
        repo.DeleteExtensionOverrideAsync("s1", "2001", Arg.Any<CancellationToken>()).Returns(true);

        await sut.DeleteExtensionOverrideAsync("s1", "2001");

        await repo.Received(1).AppendAuditLogAsync(
            Arg.Is<CosAuditEntry>(e =>
                e.ServerId == "s1" &&
                e.EntityType == "extension" &&
                e.EntityId == "2001" &&
                e.Action == "override-removed"),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // SimulateDialAsync (stub)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateDialAsync_ShouldReturnError_WhenNotInitialized()
    {
        var sut = CreateService(out _, out _);

        var result = await sut.SimulateDialAsync("s1", "2001", "5551234");

        result.Verdict.Should().Be(DialSimulatorVerdict.Error);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static CosService CreateService(
        out ICosRepository cosRepo,
        out ICosPatternGroupRepository patternRepo,
        IConfiguration? config = null)
    {
        cosRepo = Substitute.For<ICosRepository>();
        patternRepo = Substitute.For<ICosPatternGroupRepository>();
        var resolver = Substitute.For<ICosRepositoryResolver>();
        resolver.GetCosRepository(Arg.Any<string>()).Returns(cosRepo);
        resolver.GetPatternGroupRepository(Arg.Any<string>()).Returns(patternRepo);
        config ??= new ConfigurationBuilder().Build();
        var logger = Substitute.For<ILogger<CosService>>();
        return new CosService(resolver, config, logger);
    }

    private static IConfiguration BuildConfig(int defaultCosLevelId)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Asterisk:Servers:0:Id"] = "s1",
            ["Asterisk:Servers:0:DefaultCosLevelId"] = defaultCosLevelId.ToString(CultureInfo.InvariantCulture),
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }
}
