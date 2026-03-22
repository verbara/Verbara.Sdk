using Bunit;
using FluentAssertions;
using PbxAdmin.Components.Shared;

namespace PbxAdmin.Tests.Components;

public sealed class ButtonSpinnerTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    [Fact]
    public void ShouldRenderChildContent()
    {
        var cut = _ctx.Render<ButtonSpinner>(p => p
            .Add(c => c.Loading, false)
            .AddChildContent("Save"));
        cut.Markup.Should().Contain("Save");
        cut.Find("button").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ShouldShowSpinnerWhenLoading()
    {
        var cut = _ctx.Render<ButtonSpinner>(p => p
            .Add(c => c.Loading, true)
            .Add(c => c.LoadingText, "Saving..."));
        cut.Find(".btn-spinner").Should().NotBeNull();
        cut.Markup.Should().Contain("Saving...");
    }

    [Fact]
    public void ShouldBeDisabledWhenLoading()
    {
        var cut = _ctx.Render<ButtonSpinner>(p => p.Add(c => c.Loading, true));
        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void ShouldApplyCssClass()
    {
        var cut = _ctx.Render<ButtonSpinner>(p => p
            .Add(c => c.Loading, false)
            .Add(c => c.CssClass, "btn-sm btn-red")
            .AddChildContent("Delete"));
        cut.Find("button").ClassList.Should().Contain("btn-red");
    }

    public void Dispose() => _ctx.Dispose();
}
