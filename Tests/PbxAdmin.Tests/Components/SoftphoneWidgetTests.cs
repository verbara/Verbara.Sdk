using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using NSubstitute;
using PbxAdmin.Components.Shared;
using PbxAdmin.Models;
using PbxAdmin.Resources;
using PbxAdmin.Services;

namespace PbxAdmin.Tests.Components;

public sealed class SoftphoneWidgetTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();

    public SoftphoneWidgetTests()
    {
        // SoftphoneService only uses WebRtcProviderResolver inside ConnectAsync.
        // These tests do not call ConnectAsync, so null! is safe for the resolver parameter.
        var jsRuntime = Substitute.For<IJSRuntime>();
        var toast = Substitute.For<IToastService>();
        var options = Options.Create(new SoftphoneOptions());

        _ctx.Services.AddScoped<SoftphoneService>(_ =>
            new SoftphoneService(jsRuntime, toast, null!, options));

        var serverSvc = Substitute.For<ISelectedServerService>();
        serverSvc.SelectedServerId.Returns("pbx-test");
        _ctx.Services.AddSingleton(serverSvc);

        var localizer = Substitute.For<IStringLocalizer<SharedStrings>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _ctx.Services.AddSingleton(localizer);
    }

    [Fact]
    public void ShouldRenderFab()
    {
        var cut = _ctx.Render<SoftphoneWidget>();
        cut.Find(".softphone-fab").Should().NotBeNull();
    }

    [Fact]
    public void ShouldHaveUnregisteredClassByDefault()
    {
        var cut = _ctx.Render<SoftphoneWidget>();
        cut.Find(".softphone-fab").ClassList.Should().Contain("unregistered");
    }

    [Fact]
    public void ShouldExpandOnClick()
    {
        var cut = _ctx.Render<SoftphoneWidget>();
        cut.Find(".softphone-fab").Click();
        cut.Find(".softphone-popup").Should().NotBeNull();
    }

    [Fact]
    public void ShouldShowConnectButtonWhenUnregistered()
    {
        var cut = _ctx.Render<SoftphoneWidget>();
        cut.Find(".softphone-fab").Click();
        cut.Find(".softphone-connect").Should().NotBeNull();
    }

    public async ValueTask DisposeAsync() => await _ctx.DisposeAsync();
}
