using PbxAdmin.Models;
using PbxAdmin.Services;
using PbxAdmin.Services.Dialplan;
using PbxAdmin.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace PbxAdmin.Tests.Services;

public class IvrMenuServiceTests
{
    private const string ServerId = "srv1";

    [Fact]
    public async Task Create_ShouldSucceed_WhenValid()
    {
        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "main", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([]);
        repo.CreateMenuAsync(Arg.Any<IvrMenuConfig>(), Arg.Any<CancellationToken>()).Returns(1);

        var (success, error) = await sut.CreateMenuAsync(ServerId, ValidMenu());
        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task Create_ShouldSucceed_WhenZeroItems()
    {
        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "main", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([]);
        repo.CreateMenuAsync(Arg.Any<IvrMenuConfig>(), Arg.Any<CancellationToken>()).Returns(1);

        var config = ValidMenu() with { Items = [] };
        var (success, _) = await sut.CreateMenuAsync(ServerId, config);
        success.Should().BeTrue();
    }

    [Fact]
    public async Task Create_ShouldFail_WhenNameDuplicate()
    {
        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "main", Arg.Any<CancellationToken>()).Returns(ValidMenu());

        var (success, error) = await sut.CreateMenuAsync(ServerId, ValidMenu());
        success.Should().BeFalse();
        error.Should().Contain("already exists");
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid name!")]
    [InlineData("spaces here")]
    public void Create_ShouldFail_WhenNameInvalid(string name)
    {
        var config = ValidMenu() with { Name = name };
        var error = IvrMenuService.ValidateMenu(config);
        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("valid-name", true)]
    [InlineData("menu123", true)]
    [InlineData("a", true)]
    public void Validate_ShouldAcceptValidNames(string name, bool expected)
    {
        var config = ValidMenu() with { Name = name };
        var error = IvrMenuService.ValidateMenu(config);
        (error is null).Should().Be(expected);
    }

    [Fact]
    public void Create_ShouldFail_WhenDigitDuplicate()
    {
        var config = ValidMenu() with
        {
            Items = [
                new() { Digit = "1", DestType = "extension", DestTarget = "1001" },
                new() { Digit = "1", DestType = "extension", DestTarget = "1002" }
            ]
        };
        var error = IvrMenuService.ValidateMenu(config);
        error.Should().Contain("Duplicate digit");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("10")]
    [InlineData("")]
    public void Create_ShouldFail_WhenDigitInvalid(string digit)
    {
        var config = ValidMenu() with
        {
            Items = [new() { Digit = digit, DestType = "extension", DestTarget = "1001" }]
        };
        var error = IvrMenuService.ValidateMenu(config);
        error.Should().Contain("Invalid digit");
    }

    [Fact]
    public void Create_ShouldFail_WhenDestTypeInvalid()
    {
        var config = ValidMenu() with
        {
            Items = [new() { Digit = "1", DestType = "unknown", DestTarget = "1001" }]
        };
        var error = IvrMenuService.ValidateMenu(config);
        error.Should().Contain("Invalid destination type");
    }

    [Fact]
    public async Task Create_ShouldFail_WhenIvrDestNotExists()
    {
        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "main", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([]);

        var config = ValidMenu() with
        {
            Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "nonexistent" }]
        };
        var (success, error) = await sut.CreateMenuAsync(ServerId, config);
        success.Should().BeFalse();
        error.Should().Contain("does not exist");
    }

    [Fact]
    public async Task Create_ShouldFail_WhenCycleDetected()
    {
        var menuA = ValidMenu() with { Name = "a", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "b" }] };
        var menuB = new IvrMenuConfig { Id = 2, ServerId = ServerId, Name = "b", Label = "B", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "a" }] };

        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "a", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([menuB]);

        var (success, error) = await sut.CreateMenuAsync(ServerId, menuA);
        success.Should().BeFalse();
        error.Should().Contain("Circular");
    }

    [Fact]
    public async Task Create_ShouldSucceed_WhenDiamondPattern()
    {
        var menuB = new IvrMenuConfig { Id = 2, ServerId = ServerId, Name = "b", Label = "B", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "d" }] };
        var menuC = new IvrMenuConfig { Id = 3, ServerId = ServerId, Name = "c", Label = "C", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "d" }] };
        var menuD = new IvrMenuConfig { Id = 4, ServerId = ServerId, Name = "d", Label = "D", Items = [] };

        var menuA = ValidMenu() with
        {
            Name = "a",
            Items = [
                new() { Digit = "1", DestType = "ivr", DestTarget = "b" },
                new() { Digit = "2", DestType = "ivr", DestTarget = "c" }
            ]
        };

        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "a", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([menuB, menuC, menuD]);
        repo.CreateMenuAsync(Arg.Any<IvrMenuConfig>(), Arg.Any<CancellationToken>()).Returns(1);

        var (success, _) = await sut.CreateMenuAsync(ServerId, menuA);
        success.Should().BeTrue();
    }

    [Fact]
    public async Task Create_ShouldFail_WhenDepthExceeds5()
    {
        var menus = new List<IvrMenuConfig>();
        var names = new[] { "b", "c", "d", "e", "f" };
        for (var i = 0; i < names.Length; i++)
        {
            var next = i < names.Length - 1 ? names[i + 1] : null;
            var items = next is not null
                ? new List<IvrMenuItemConfig> { new() { Digit = "1", DestType = "ivr", DestTarget = next } }
                : new List<IvrMenuItemConfig>();
            menus.Add(new IvrMenuConfig { Id = i + 2, ServerId = ServerId, Name = names[i], Label = names[i], Items = items });
        }

        var menuA = ValidMenu() with { Name = "a", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "b" }] };

        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "a", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns(menus);

        var (success, error) = await sut.CreateMenuAsync(ServerId, menuA);
        success.Should().BeFalse();
        error.Should().Contain("exceeds maximum");
    }

    [Fact]
    public async Task Create_ShouldWarn_WhenDepthExceeds3()
    {
        var menuB = new IvrMenuConfig { Id = 2, ServerId = ServerId, Name = "b", Label = "B", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "c" }] };
        var menuC = new IvrMenuConfig { Id = 3, ServerId = ServerId, Name = "c", Label = "C", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "d" }] };
        var menuD = new IvrMenuConfig { Id = 4, ServerId = ServerId, Name = "d", Label = "D", Items = [] };

        var menuA = ValidMenu() with { Name = "a", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "b" }] };

        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "a", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([menuB, menuC, menuD]);
        repo.CreateMenuAsync(Arg.Any<IvrMenuConfig>(), Arg.Any<CancellationToken>()).Returns(1);

        var (success, _) = await sut.CreateMenuAsync(ServerId, menuA);
        success.Should().BeTrue();
    }

    [Fact]
    public async Task Create_ShouldFail_WhenExternalDestNoTrunk()
    {
        var (sut, repo, _) = CreateService();
        repo.GetMenuByNameAsync(ServerId, "main", Arg.Any<CancellationToken>()).Returns((IvrMenuConfig?)null);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([]);

        var config = ValidMenu() with
        {
            Items = [new() { Digit = "1", DestType = "external", DestTarget = "5551234", Trunk = null }]
        };
        var (success, error) = await sut.CreateMenuAsync(ServerId, config);
        success.Should().BeFalse();
        error.Should().Contain("trunk");
    }

    [Fact]
    public async Task Delete_ShouldFail_WhenReferencedByIvr()
    {
        var (sut, repo, _) = CreateService();
        repo.IsMenuReferencedAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteMenuAsync(ServerId, 1);
        success.Should().BeFalse();
        error.Should().Contain("referenced");
    }

    [Fact]
    public async Task Delete_ShouldFail_WhenReferencedByRoute()
    {
        var (sut, repo, _) = CreateService();
        repo.IsMenuReferencedAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteMenuAsync(ServerId, 1);
        success.Should().BeFalse();
        error.Should().Contain("referenced");
    }

    [Fact]
    public async Task Delete_ShouldFail_WhenReferencedByTimeCondition()
    {
        var (sut, repo, _) = CreateService();
        repo.IsMenuReferencedAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var (success, error) = await sut.DeleteMenuAsync(ServerId, 1);
        success.Should().BeFalse();
        error.Should().Contain("referenced");
    }

    [Fact]
    public async Task GetRootMenus_ShouldExcludeReferenced()
    {
        var menuA = ValidMenu() with { Name = "main", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "sub" }] };
        var menuB = new IvrMenuConfig { Id = 2, ServerId = ServerId, Name = "sub", Label = "Sub", Items = [] };

        var (sut, repo, _) = CreateService();
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([menuA, menuB]);

        var roots = await sut.GetRootMenusAsync(ServerId);
        roots.Should().HaveCount(1);
        roots[0].Name.Should().Be("main");
    }

    [Fact]
    public async Task GetTree_ShouldBuildHierarchy()
    {
        var menuA = ValidMenu() with { Name = "main", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "sub" }, new() { Digit = "0", DestType = "extension", DestTarget = "1000" }] };
        var menuB = new IvrMenuConfig { Id = 2, ServerId = ServerId, Name = "sub", Label = "Sub Menu", Items = [] };

        var (sut, repo, _) = CreateService();
        repo.GetMenuAsync(1, Arg.Any<CancellationToken>()).Returns(menuA);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([menuA, menuB]);

        var tree = await sut.GetTreeAsync(1);
        tree.Should().NotBeNull();
        tree!.Name.Should().Be("main");
        tree.Children.Should().HaveCount(2);
        tree.Children.Should().Contain(c => c.Name == "sub" && c.Digit == "1");
        tree.Children.Should().Contain(c => c.Label == "Ext 1000" && c.Digit == "0");
    }

    [Fact]
    public async Task GetTree_ShouldHandleCycleGracefully()
    {
        var menuA = ValidMenu() with { Name = "a", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "b" }] };
        var menuB = new IvrMenuConfig { Id = 2, ServerId = ServerId, Name = "b", Label = "B", Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "a" }] };

        var (sut, repo, _) = CreateService();
        repo.GetMenuAsync(1, Arg.Any<CancellationToken>()).Returns(menuA);
        repo.GetMenusAsync(ServerId, Arg.Any<CancellationToken>()).Returns([menuA, menuB]);

        var tree = await sut.GetTreeAsync(1);
        tree.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateGreeting_ShouldReturnWarning_WhenNotFound()
    {
        var (sut, _, _) = CreateService();
        var (_, warning) = await sut.ValidateGreetingAsync(ServerId, "nonexistent");
        warning.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateGreeting_ShouldSucceed_WhenConnectionFails()
    {
        var (sut, _, _) = CreateService();
        var (exists, _) = await sut.ValidateGreetingAsync(ServerId, "some-file");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateGreeting_ShouldSucceed_WhenEmpty()
    {
        var (sut, _, _) = CreateService();
        var (exists, warning) = await sut.ValidateGreetingAsync(ServerId, "");
        exists.Should().BeTrue();
        warning.Should().BeNull();
    }

    // ─── Helpers ───

    private static (IvrMenuService Sut, IIvrMenuRepository Repo, DialplanRegenerator Regen) CreateService()
    {
        var repo = Substitute.For<IIvrMenuRepository>();
        var routeRepoResolver = Substitute.For<IRouteRepositoryResolver>();
        var dialplanResolver = Substitute.For<IDialplanProviderResolver>();
        var regen = new DialplanRegenerator(routeRepoResolver, dialplanResolver, repo);
        var logger = Substitute.For<ILogger<IvrMenuService>>();
        var soundSvc = new SystemSoundService(Substitute.For<IConfiguration>(), new AudioFileService(NullLogger<AudioFileService>.Instance));
        var sut = new IvrMenuService(repo, regen, soundSvc, logger);
        return (sut, repo, regen);
    }

    private static IvrMenuConfig ValidMenu() => new()
    {
        Id = 1, ServerId = ServerId, Name = "main", Label = "Main Menu",
        Greeting = "welcome", Timeout = 5, MaxRetries = 3, Enabled = true,
        Items = [
            new() { Digit = "1", DestType = "extension", DestTarget = "1001" },
            new() { Digit = "2", DestType = "queue", DestTarget = "sales" }
        ]
    };
}
