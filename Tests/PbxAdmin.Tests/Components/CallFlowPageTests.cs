using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Sessions.Manager;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PbxAdmin.Components.Pages;
using PbxAdmin.Models;
using PbxAdmin.Resources;
using PbxAdmin.Services;
using PbxAdmin.Services.CallFlow;
using PbxAdmin.Services.Dialplan;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Tests.Components;

public sealed class CallFlowPageTests : IDisposable
{
    private const string ServerId = "server1";
    private readonly BunitContext _ctx = new();
    private readonly IRouteRepository _routeRepo;
    private readonly IIvrMenuRepository _ivrRepo;
    private readonly IQueueConfigService _queueSvc;
    private readonly IExtensionService _extensionSvc;
    private readonly ITrunkService _trunkSvc;

    public CallFlowPageTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Server selection
        var serverSvc = Substitute.For<ISelectedServerService>();
        serverSvc.SelectedServerId.Returns(ServerId);

        // Localizer
        var localizer = Substitute.For<IStringLocalizer<SharedStrings>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // Route repository (returns empty by default)
        _routeRepo = Substitute.For<IRouteRepository>();
        _routeRepo.GetInboundRoutesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<InboundRouteConfig>()));
        _routeRepo.GetOutboundRoutesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<OutboundRouteConfig>()));
        _routeRepo.GetTimeConditionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TimeConditionConfig>()));

        var repoResolver = Substitute.For<IRouteRepositoryResolver>();
        repoResolver.GetRepository(Arg.Any<string>()).Returns(_routeRepo);

        var dialplanResolver = Substitute.For<IDialplanProviderResolver>();

        // AsteriskMonitorService (needed by RouteService and TimeConditionService)
        var monitor = new AsteriskMonitorService(
            Substitute.For<IAmiConnectionFactory>(),
            NullLoggerFactory.Instance,
            new EventLogService(),
            Substitute.For<ICallSessionManager>(),
            new ConfigurationBuilder().Build(),
            NullLogger<AsteriskMonitorService>.Instance,
            Substitute.For<IServiceProvider>());

        var regenerator = new DialplanRegenerator(repoResolver, dialplanResolver, Substitute.For<IIvrMenuRepository>());

        var routeService = new RouteService(
            repoResolver, regenerator, monitor,
            NullLogger<RouteService>.Instance,
            Substitute.For<IServiceProvider>());

        var tcService = new TimeConditionService(
            repoResolver, regenerator, monitor,
            NullLogger<TimeConditionService>.Instance,
            Substitute.For<IServiceProvider>());

        // Interface-based service mocks
        _ivrRepo = Substitute.For<IIvrMenuRepository>();
        _ivrRepo.GetMenusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<IvrMenuConfig>()));

        _queueSvc = Substitute.For<IQueueConfigService>();
        _queueSvc.GetQueuesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<QueueConfigDto>()));

        _extensionSvc = Substitute.For<IExtensionService>();
        _extensionSvc.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ExtensionViewModel>()));

        _trunkSvc = Substitute.For<ITrunkService>();
        _trunkSvc.GetTrunksAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TrunkViewModel>()));

        var callFlowService = new CallFlowService(
            routeService, tcService, _ivrRepo, _queueSvc,
            _extensionSvc, _trunkSvc,
            NullLogger<CallFlowService>.Instance);

        _ctx.Services.AddSingleton(serverSvc);
        _ctx.Services.AddSingleton(localizer);
        _ctx.Services.AddSingleton(callFlowService);
    }

    [Fact]
    public void CallFlowPage_ShouldRenderKpiCards()
    {
        var cut = _ctx.Render<CallFlow>();

        cut.Markup.Should().Contain("CF_ActiveDids");
        cut.Markup.Should().Contain("CF_TimeConditions");
        cut.Markup.Should().Contain("CF_QueueHealth");
        cut.Markup.Should().Contain("CF_TrunkStatus");
    }

    [Fact]
    public void CallFlowPage_ShouldShowNoWarnings_WhenHealthy()
    {
        var cut = _ctx.Render<CallFlow>();

        cut.Markup.Should().Contain("CF_AllHealthy");
    }

    [Fact]
    public void CallFlowPage_ShouldShowNoDids_WhenEmpty()
    {
        var cut = _ctx.Render<CallFlow>();

        cut.Markup.Should().Contain("CF_NoDids");
    }

    [Fact]
    public void CallFlowPage_ShouldShowSelectMessage_WhenNoDidSelected()
    {
        var cut = _ctx.Render<CallFlow>();

        cut.Markup.Should().Contain("CF_SelectDid");
    }

    [Fact]
    public void CallFlowPage_ShouldRenderDidList_WhenRoutesExist()
    {
        // Arrange: return a route
        _routeRepo.GetInboundRoutesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<InboundRouteConfig>
            {
                new()
                {
                    Id = 1, ServerId = ServerId, Name = "Main Line",
                    DidPattern = "5551234", DestinationType = "extension",
                    Destination = "1001", Priority = 100, Enabled = true,
                },
            }));

        _extensionSvc.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ExtensionViewModel>
            {
                new() { Extension = "1001", Name = "John Doe", Status = ExtensionStatus.Registered, Technology = ExtensionTechnology.PjSip },
            }));

        var cut = _ctx.Render<CallFlow>();

        cut.Markup.Should().Contain("5551234");
        cut.Markup.Should().Contain("Main Line");
    }

    public void Dispose() => _ctx.Dispose();
}
