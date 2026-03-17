using DashboardExample.Models;
using DashboardExample.Services;
using DashboardExample.Services.Dialplan;
using DashboardExample.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DashboardExample.Tests.Services;

public class RouteServiceTests
{
    [Theory]
    [InlineData("_NXXNXXXXXX", true)]
    [InlineData("_00X.", true)]
    [InlineData("911", true)]
    [InlineData("_1NXXNXXXXXX", true)]
    [InlineData("_[2-9]XXXXXXX", true)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("_", false)]
    public void IsValidDialPattern_ShouldValidateCorrectly(string pattern, bool expected)
    {
        RouteService.IsValidDialPattern(pattern).Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // Inbound route validation tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateInbound_ShouldReject_WhenDidPatternEmpty()
    {
        var sut = CreateRouteService();
        var config = new InboundRouteConfig
        {
            ServerId = "s1",
            DidPattern = "",
            DestinationType = "extension",
            Destination = "100",
        };

        var (success, error) = await sut.CreateInboundRouteAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("DID pattern");
    }

    [Fact]
    public async Task CreateInbound_ShouldReject_WhenDestinationMissing()
    {
        var sut = CreateRouteService();
        var config = new InboundRouteConfig
        {
            ServerId = "s1",
            DidPattern = "5551234",
            DestinationType = "",
            Destination = "",
        };

        var (success, error) = await sut.CreateInboundRouteAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("Destination");
    }

    // -----------------------------------------------------------------------
    // Outbound route validation tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateOutbound_ShouldReject_WhenInvalidPattern()
    {
        var sut = CreateRouteService();
        var config = new OutboundRouteConfig
        {
            ServerId = "s1",
            DialPattern = "abc",
            Trunks = [new RouteTrunk { TrunkName = "t1", TrunkTechnology = "PjSip", Sequence = 1 }],
        };

        var (success, error) = await sut.CreateOutboundRouteAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("Invalid dial pattern");
    }

    [Fact]
    public async Task CreateOutbound_ShouldReject_WhenNoTrunks()
    {
        var sut = CreateRouteService();
        var config = new OutboundRouteConfig
        {
            ServerId = "s1",
            DialPattern = "_NXXNXXXXXX",
            Trunks = [],
        };

        var (success, error) = await sut.CreateOutboundRouteAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("trunk");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static RouteService CreateRouteService()
    {
        var repo = Substitute.For<IRouteRepository>();
        var repoResolver = Substitute.For<IRouteRepositoryResolver>();
        repoResolver.GetRepository(Arg.Any<string>()).Returns(repo);

        var dialplanResolver = Substitute.For<IDialplanProviderResolver>();
        var ivrRepo = Substitute.For<IIvrMenuRepository>();
        var regenerator = new DialplanRegenerator(repoResolver, dialplanResolver, ivrRepo);
        var logger = Substitute.For<ILogger<RouteService>>();

        // AsteriskMonitorService and TrunkService are not accessed during validation
        return new RouteService(repoResolver, regenerator, null!, null!, logger);
    }
}
