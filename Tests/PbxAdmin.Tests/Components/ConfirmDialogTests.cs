using Bunit;
using FluentAssertions;
using PbxAdmin.Components.Shared;

namespace PbxAdmin.Tests.Components;

public sealed class ConfirmDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    [Fact]
    public void ShouldBeHiddenByDefault()
    {
        var cut = _ctx.Render<ConfirmDialog>();
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void ShouldShowWhenTriggered()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.Title, "Delete?")
            .Add(c => c.Message, "Are you sure?"));
        cut.Instance.Show();
        cut.Render();
        cut.Markup.Should().Contain("Delete?");
        cut.Markup.Should().Contain("Are you sure?");
        cut.Find(".confirm-dialog").Should().NotBeNull();
    }

    [Fact]
    public void ShouldFireOnConfirm()
    {
        var confirmed = false;
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.OnConfirm, () => { confirmed = true; }));
        cut.Instance.Show();
        cut.Render();
        cut.Find(".btn-red").Click();
        confirmed.Should().BeTrue();
    }

    [Fact]
    public void ShouldHideAfterConfirm()
    {
        var cut = _ctx.Render<ConfirmDialog>();
        cut.Instance.Show();
        cut.Render();
        cut.Find(".btn-red").Click();
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void ShouldHideOnCancel()
    {
        var cut = _ctx.Render<ConfirmDialog>();
        cut.Instance.Show();
        cut.Render();
        // Cancel button is the first .btn.btn-sm (before .btn-red)
        cut.FindAll("button.btn-sm")[0].Click();
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void ShouldUseWarningStyle()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p.Add(c => c.ConfirmStyle, "warning"));
        cut.Instance.Show();
        cut.Render();
        cut.Find(".btn-yellow").Should().NotBeNull();
    }

    public void Dispose() => _ctx.Dispose();
}
