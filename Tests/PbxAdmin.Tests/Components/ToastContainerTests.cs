using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PbxAdmin.Components.Shared;
using PbxAdmin.Models;
using PbxAdmin.Services;

namespace PbxAdmin.Tests.Components;

public sealed class ToastContainerTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ToastContainerTests()
    {
        _ctx.Services.AddScoped<IToastService, ToastService>();
    }

    [Fact]
    public void ShouldRenderEmptyWhenNoToasts()
    {
        var cut = _ctx.Render<ToastContainer>();
        cut.Find(".toast-container").ChildElementCount.Should().Be(0);
    }

    [Fact]
    public void ShouldRenderToastWhenShown()
    {
        var toast = _ctx.Services.GetRequiredService<IToastService>();
        var cut = _ctx.Render<ToastContainer>();
        toast.Show("Test toast", ToastLevel.Success);
        cut.WaitForState(() => cut.Find(".toast-container").ChildElementCount > 0);
        cut.Markup.Should().Contain("Test toast");
        cut.Markup.Should().Contain("toast-success");
    }

    [Fact]
    public void ShouldRenderErrorToastWithDetail()
    {
        var toast = _ctx.Services.GetRequiredService<IToastService>();
        var cut = _ctx.Render<ToastContainer>();
        toast.Show("Error!", ToastLevel.Error, "Details here");
        cut.WaitForState(() => cut.Find(".toast-container").ChildElementCount > 0);
        cut.Markup.Should().Contain("toast-error");
        cut.Markup.Should().Contain("Details here");
    }

    public void Dispose() => _ctx.Dispose();
}
