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
using PbxAdmin.Services.Repositories;

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

        var cosRepo = Substitute.For<ICosRepository>();
        cosRepo.GetLevelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CosLevel>()));
        cosRepo.GetExtensionOverridesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CosExtensionOverride>()));

        var cosRepoResolver = Substitute.For<ICosRepositoryResolver>();
        cosRepoResolver.GetCosRepository(Arg.Any<string>()).Returns(cosRepo);

        var config = new ConfigurationBuilder().Build();
        var cosSvc = new CosService(cosRepoResolver, config, NullLogger<CosService>.Instance);

        _ctx.Services.AddSingleton(serverSvc);
        _ctx.Services.AddSingleton(extSvc);
        _ctx.Services.AddSingleton(configOp);
        _ctx.Services.AddSingleton(localizer);
        _ctx.Services.AddSingleton(templateSvc);
        _ctx.Services.AddSingleton(toastSvc);
        _ctx.Services.AddSingleton(cosSvc);
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
