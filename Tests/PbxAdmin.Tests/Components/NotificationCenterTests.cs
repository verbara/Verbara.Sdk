using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using PbxAdmin.Components.Shared;
using PbxAdmin.Models;
using PbxAdmin.Resources;
using PbxAdmin.Services;

namespace PbxAdmin.Tests.Components;

public sealed class NotificationCenterTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public NotificationCenterTests()
    {
        _ctx.Services.AddScoped<IToastService, ToastService>();
        var localizer = Substitute.For<IStringLocalizer<SharedStrings>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _ctx.Services.AddSingleton(localizer);
    }

    [Fact]
    public void ShouldRenderBellIcon()
    {
        var cut = _ctx.Render<NotificationCenter>();
        cut.Find(".notification-bell").Should().NotBeNull();
    }

    [Fact]
    public void ShouldShowBadgeWhenUnread()
    {
        var toast = _ctx.Services.GetRequiredService<IToastService>();
        toast.Show("Test", ToastLevel.Info);
        var cut = _ctx.Render<NotificationCenter>();
        cut.Find(".notification-badge").TextContent.Should().Be("1");
    }

    [Fact]
    public void ShouldOpenPanelOnClick()
    {
        var toast = _ctx.Services.GetRequiredService<IToastService>();
        toast.Show("Test", ToastLevel.Info);
        var cut = _ctx.Render<NotificationCenter>();
        cut.Find(".notification-bell").Click();
        cut.Find(".notification-panel").Should().NotBeNull();
    }

    [Fact]
    public void ShouldMarkReadOnOpen()
    {
        var toast = _ctx.Services.GetRequiredService<IToastService>();
        toast.Show("Test", ToastLevel.Info);
        var cut = _ctx.Render<NotificationCenter>();
        cut.Find(".notification-bell").Click();
        toast.UnreadCount.Should().Be(0);
    }

    public void Dispose() => _ctx.Dispose();
}
