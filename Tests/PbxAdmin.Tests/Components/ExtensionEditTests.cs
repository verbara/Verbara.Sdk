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
using PbxAdmin.Models;
using PbxAdmin.Resources;
using PbxAdmin.Services;
using PbxAdmin.Services.Dialplan;

namespace PbxAdmin.Tests.Components;

public sealed class ExtensionEditTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ExtensionEditTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var serverSvc = Substitute.For<ISelectedServerService>();
        serverSvc.SelectedServerId.Returns("server1");

        var extSvc = Substitute.For<IExtensionService>();
        extSvc.GetExtensionRange(Arg.Any<string>()).Returns((Start: 100, End: 999));

        var configOp = Substitute.For<IConfigOperationState>();

        var localizer = Substitute.For<IStringLocalizer<SharedStrings>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var templateSvc = Substitute.For<IExtensionTemplateService>();
        templateSvc.GetAllAsync().Returns(Task.FromResult<IReadOnlyList<ExtensionTemplate>>([]));

        var toastSvc = Substitute.For<IToastService>();

        var monitor = new AsteriskMonitorService(
            Substitute.For<IAmiConnectionFactory>(),
            NullLoggerFactory.Instance,
            new EventLogService(),
            Substitute.For<ICallSessionManager>(),
            new ConfigurationBuilder().Build(),
            NullLogger<AsteriskMonitorService>.Instance);
        var discoverySvc = new DialplanDiscoveryService(monitor, NullLogger<DialplanDiscoveryService>.Instance);

        _ctx.Services.AddSingleton(serverSvc);
        _ctx.Services.AddSingleton(extSvc);
        _ctx.Services.AddSingleton(configOp);
        _ctx.Services.AddSingleton(localizer);
        _ctx.Services.AddSingleton(templateSvc);
        _ctx.Services.AddSingleton(toastSvc);
        _ctx.Services.AddSingleton(discoverySvc);
    }

    [Fact]
    public void NewExtension_ShouldRenderForm()
    {
        var cut = _ctx.Render<ExtensionEdit>();

        cut.Markup.Should().Contain("ExtEdit_ExtNumber");
    }

    [Fact]
    public void NewExtension_ShouldShowPasswordField()
    {
        var cut = _ctx.Render<ExtensionEdit>();

        cut.Markup.Should().Contain("type=\"password\"");
    }

    [Fact]
    public void NewExtension_ShouldShowTechnologyRadioButtons()
    {
        var cut = _ctx.Render<ExtensionEdit>();

        cut.Markup.Should().Contain("PJSIP");
        cut.Markup.Should().Contain("SIP");
        cut.Markup.Should().Contain("IAX2");
    }

    [Fact]
    public void NewExtension_SaveButton_ShouldBeDisabledWhenFormInvalid()
    {
        var cut = _ctx.Render<ExtensionEdit>();

        var saveButton = cut.Find("button.btn-green");
        saveButton.HasAttribute("disabled").Should().BeTrue();
    }

    public void Dispose() => _ctx.Dispose();
}
