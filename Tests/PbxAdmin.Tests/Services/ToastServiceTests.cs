using PbxAdmin.Models;
using PbxAdmin.Services;
using FluentAssertions;

namespace PbxAdmin.Tests.Services;

public class ToastServiceTests
{
    [Fact]
    public void Show_ShouldAddToast()
    {
        var svc = new ToastService();

        svc.Show("Hello");

        svc.ActiveToasts.Should().HaveCount(1);
        svc.ActiveToasts[0].Title.Should().Be("Hello");
    }

    [Fact]
    public void Show_ShouldAddNotification()
    {
        var svc = new ToastService();

        svc.Show("Hello");

        svc.Notifications.Should().HaveCount(1);
        svc.UnreadCount.Should().Be(1);
    }

    [Fact]
    public void Show_Error_ShouldNotAutoDismiss()
    {
        var svc = new ToastService();

        svc.Show("Oops", ToastLevel.Error);

        svc.ActiveToasts[0].AutoDismiss.Should().BeFalse();
    }

    [Fact]
    public void Show_Success_ShouldAutoDismiss()
    {
        var svc = new ToastService();

        svc.Show("Done", ToastLevel.Success);

        svc.ActiveToasts[0].AutoDismiss.Should().BeTrue();
    }

    [Fact]
    public void Dismiss_ShouldRemoveToast()
    {
        var svc = new ToastService();
        svc.Show("Error msg", ToastLevel.Error);
        var id = svc.ActiveToasts[0].Id;

        svc.Dismiss(id);

        svc.ActiveToasts.Should().BeEmpty();
    }

    [Fact]
    public void Dismiss_ShouldKeepNotification()
    {
        var svc = new ToastService();
        svc.Show("Error msg", ToastLevel.Error);
        var id = svc.ActiveToasts[0].Id;

        svc.Dismiss(id);

        svc.Notifications.Should().HaveCount(1);
    }

    [Fact]
    public void MarkAllRead_ShouldResetUnreadCount()
    {
        var svc = new ToastService();
        svc.Show("First", ToastLevel.Error);
        svc.Show("Second", ToastLevel.Error);

        svc.MarkAllRead();

        svc.UnreadCount.Should().Be(0);
    }

    [Fact]
    public void ClearNotifications_ShouldEmptyList()
    {
        var svc = new ToastService();
        svc.Show("Something");

        svc.ClearNotifications();

        svc.Notifications.Should().BeEmpty();
    }

    [Fact]
    public void Show_ShouldEnforceMaxToasts()
    {
        var svc = new ToastService();

        for (var i = 0; i < 7; i++)
            svc.Show($"Error {i}", ToastLevel.Error);

        svc.ActiveToasts.Should().HaveCount(5);
    }

    [Fact]
    public void Show_ShouldFireOnChanged()
    {
        var svc = new ToastService();
        var fired = false;
        svc.OnChanged += () => fired = true;

        svc.Show("Event fired?");

        fired.Should().BeTrue();
    }
}
