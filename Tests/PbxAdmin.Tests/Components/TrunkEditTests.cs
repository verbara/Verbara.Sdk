using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Sessions.Manager;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PbxAdmin.Components.Pages;
using PbxAdmin.Resources;
using PbxAdmin.Services;

namespace PbxAdmin.Tests.Components;

public sealed class TrunkEditTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public TrunkEditTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var monitor = new AsteriskMonitorService(
            Substitute.For<IAmiConnectionFactory>(),
            NullLoggerFactory.Instance,
            new EventLogService(),
            Substitute.For<ICallSessionManager>(),
            new ConfigurationBuilder().Build(),
            NullLogger<AsteriskMonitorService>.Instance);

        var trunkSvc = Substitute.For<ITrunkService>();
        var configOp = Substitute.For<IConfigOperationState>();

        var localizer = Substitute.For<IStringLocalizer<SharedStrings>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _ctx.Services.AddSingleton(monitor);
        _ctx.Services.AddSingleton(trunkSvc);
        _ctx.Services.AddSingleton(configOp);
        _ctx.Services.AddSingleton(localizer);
    }

    [Fact]
    public void NewTrunk_ShouldRenderForm()
    {
        var cut = _ctx.Render<TrunkEdit>();

        cut.Markup.Should().Contain("Lbl_Name");
    }

    [Fact]
    public void NewTrunk_SaveButton_ShouldBeDisabledWhenFormInvalid()
    {
        var cut = _ctx.Render<TrunkEdit>();

        var saveButton = cut.Find("button.btn-green");
        saveButton.HasAttribute("disabled").Should().BeTrue();
    }

    public void Dispose() => _ctx.Dispose();
}
