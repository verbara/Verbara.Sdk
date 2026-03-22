using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using PbxAdmin.Components.Pages;
using PbxAdmin.Resources;
using PbxAdmin.Services;

namespace PbxAdmin.Tests.Components;

public sealed class QueueConfigEditTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public QueueConfigEditTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var queueCfgSvc = Substitute.For<IQueueConfigService>();
        var configOp = Substitute.For<IConfigOperationState>();

        var localizer = Substitute.For<IStringLocalizer<SharedStrings>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var toastSvc = Substitute.For<IToastService>();

        _ctx.Services.AddSingleton(queueCfgSvc);
        _ctx.Services.AddSingleton(configOp);
        _ctx.Services.AddSingleton(localizer);
        _ctx.Services.AddSingleton(toastSvc);
    }

    [Fact]
    public void NewQueueConfig_ShouldRenderForm()
    {
        var cut = _ctx.Render<QueueConfigEdit>(parameters => parameters
            .Add(p => p.ServerId, "server1")
            .Add(p => p.IdOrNew, "new"));

        cut.Markup.Should().Contain("Lbl_Name");
    }

    [Fact]
    public void NewQueueConfig_SaveButton_ShouldBeDisabledWhenFormInvalid()
    {
        var cut = _ctx.Render<QueueConfigEdit>(parameters => parameters
            .Add(p => p.ServerId, "server1")
            .Add(p => p.IdOrNew, "new"));

        var saveButton = cut.Find("button.btn-green");
        saveButton.HasAttribute("disabled").Should().BeTrue();
    }

    public void Dispose() => _ctx.Dispose();
}
