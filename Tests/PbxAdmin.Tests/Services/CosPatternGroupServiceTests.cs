using PbxAdmin.Models;
using PbxAdmin.Services;
using PbxAdmin.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace PbxAdmin.Tests.Services;

public class CosPatternGroupServiceTests
{
    // -----------------------------------------------------------------------
    // Pattern validation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("_911", true)]
    [InlineData("_NXXXXXX", true)]
    [InlineData("_00.", true)]
    [InlineData("112", true)]
    [InlineData("_9[01]XXXXXXX", true)]
    [InlineData("_+.", true)]
    [InlineData("_3XXXXXXXXX", true)]
    [InlineData("*97", true)]
    [InlineData("_0NXXXXXXXXX", true)]
    [InlineData("#72", true)]
    [InlineData("_!", true)]
    [InlineData("_[2-9]XX", true)]
    [InlineData("_", false)]
    [InlineData("__911", false)]
    [InlineData("_abc", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ValidatePatterns_ShouldValidate(string pattern, bool expectedValid)
    {
        var errors = CosPatternGroupService.ValidatePatterns([pattern]);

        if (expectedValid)
            errors.Should().BeEmpty();
        else
            errors.Should().ContainSingle();
    }

    [Fact]
    public void ValidatePatterns_ShouldRejectTooLong()
    {
        var pattern = "_" + new string('X', 40); // 41 chars total
        var errors = CosPatternGroupService.ValidatePatterns([pattern]);
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidatePatterns_ShouldAcceptMultipleValid()
    {
        var errors = CosPatternGroupService.ValidatePatterns(["_911", "112", "_NXXXXXX"]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePatterns_ShouldReportAllInvalid()
    {
        var errors = CosPatternGroupService.ValidatePatterns(["_911", "hello", "_abc"]);
        errors.Should().HaveCount(2);
    }

    // -----------------------------------------------------------------------
    // CRUD: CreateGroupAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateGroupAsync_ShouldReject_WhenNameEmpty()
    {
        var sut = CreateService(out _);
        var group = new CosPatternGroup { ServerId = "s1", Name = "", Patterns = ["_911"] };

        var (success, error) = await sut.CreateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task CreateGroupAsync_ShouldReject_WhenNameWhitespace()
    {
        var sut = CreateService(out _);
        var group = new CosPatternGroup { ServerId = "s1", Name = "   ", Patterns = ["_911"] };

        var (success, error) = await sut.CreateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task CreateGroupAsync_ShouldReject_WhenNoPatternsProvided()
    {
        var sut = CreateService(out _);
        var group = new CosPatternGroup { ServerId = "s1", Name = "Emergency", Patterns = [] };

        var (success, error) = await sut.CreateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().Contain("pattern");
    }

    [Fact]
    public async Task CreateGroupAsync_ShouldReject_WhenPatternInvalid()
    {
        var sut = CreateService(out _);
        var group = new CosPatternGroup { ServerId = "s1", Name = "Emergency", Patterns = ["hello"] };

        var (success, error) = await sut.CreateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateGroupAsync_ShouldReject_WhenDuplicateName()
    {
        var sut = CreateService(out var repo);
        repo.GetGroupsAsync("s1", Arg.Any<CancellationToken>())
            .Returns([new CosPatternGroup { Id = 1, ServerId = "s1", Name = "Emergency", Patterns = ["_911"] }]);

        var group = new CosPatternGroup { ServerId = "s1", Name = "Emergency", Patterns = ["_911"] };

        var (success, error) = await sut.CreateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().Contain("exists");
    }

    [Fact]
    public async Task CreateGroupAsync_ShouldSucceed_WhenValid()
    {
        var sut = CreateService(out var repo);
        repo.GetGroupsAsync("s1", Arg.Any<CancellationToken>()).Returns([]);
        repo.CreateGroupAsync(Arg.Any<CosPatternGroup>(), Arg.Any<CancellationToken>()).Returns(42);

        var group = new CosPatternGroup { ServerId = "s1", Name = "Emergency", Patterns = ["_911", "112"] };

        var (success, error) = await sut.CreateGroupAsync(group);

        success.Should().BeTrue();
        error.Should().BeNull();
        await repo.Received(1).CreateGroupAsync(group, Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // CRUD: UpdateGroupAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateGroupAsync_ShouldReject_WhenPatternInvalid()
    {
        var sut = CreateService(out _);
        var group = new CosPatternGroup { Id = 1, ServerId = "s1", Name = "Emergency", Patterns = ["_abc"] };

        var (success, error) = await sut.UpdateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateGroupAsync_ShouldReject_WhenNameEmpty()
    {
        var sut = CreateService(out _);
        var group = new CosPatternGroup { Id = 1, ServerId = "s1", Name = "", Patterns = ["_911"] };

        var (success, error) = await sut.UpdateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task UpdateGroupAsync_ShouldSucceed_WhenValid()
    {
        var sut = CreateService(out var repo);
        repo.UpdateGroupAsync(Arg.Any<CosPatternGroup>(), Arg.Any<CancellationToken>()).Returns(true);

        var group = new CosPatternGroup { Id = 1, ServerId = "s1", Name = "Emergency", Patterns = ["_911"] };

        var (success, error) = await sut.UpdateGroupAsync(group);

        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task UpdateGroupAsync_ShouldFail_WhenNotFound()
    {
        var sut = CreateService(out var repo);
        repo.UpdateGroupAsync(Arg.Any<CosPatternGroup>(), Arg.Any<CancellationToken>()).Returns(false);

        var group = new CosPatternGroup { Id = 99, ServerId = "s1", Name = "Emergency", Patterns = ["_911"] };

        var (success, error) = await sut.UpdateGroupAsync(group);

        success.Should().BeFalse();
        error.Should().Contain("not found");
    }

    // -----------------------------------------------------------------------
    // CRUD: DeleteGroupAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteGroupAsync_ShouldReject_WhenReferenced()
    {
        var sut = CreateService(out var repo);
        repo.IsGroupReferencedAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteGroupAsync(1, "s1");

        success.Should().BeFalse();
        error.Should().Contain("referenced");
    }

    [Fact]
    public async Task DeleteGroupAsync_ShouldSucceed_WhenNotReferenced()
    {
        var sut = CreateService(out var repo);
        repo.IsGroupReferencedAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        repo.DeleteGroupAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteGroupAsync(1, "s1");

        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task DeleteGroupAsync_ShouldFail_WhenNotFound()
    {
        var sut = CreateService(out var repo);
        repo.IsGroupReferencedAsync(99, Arg.Any<CancellationToken>()).Returns(false);
        repo.DeleteGroupAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var (success, error) = await sut.DeleteGroupAsync(99, "s1");

        success.Should().BeFalse();
        error.Should().Contain("not found");
    }

    // -----------------------------------------------------------------------
    // CRUD: GetGroupsAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetGroupsAsync_ShouldReturnFromRepo()
    {
        var sut = CreateService(out var repo);
        var expected = new List<CosPatternGroup>
        {
            new() { Id = 1, ServerId = "s1", Name = "Emergency", Patterns = ["_911"] },
            new() { Id = 2, ServerId = "s1", Name = "Local", Patterns = ["_NXXXXXX"] },
        };
        repo.GetGroupsAsync("s1", Arg.Any<CancellationToken>()).Returns(expected);

        var result = await sut.GetGroupsAsync("s1");

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(expected);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static CosPatternGroupService CreateService(out ICosPatternGroupRepository repo)
    {
        repo = Substitute.For<ICosPatternGroupRepository>();
        var resolver = Substitute.For<ICosRepositoryResolver>();
        resolver.GetPatternGroupRepository(Arg.Any<string>()).Returns(repo);
        var logger = Substitute.For<ILogger<CosPatternGroupService>>();
        return new CosPatternGroupService(resolver, logger);
    }
}
