using PbxAdmin.Models;

namespace PbxAdmin.Services;

public interface IToastService
{
    IReadOnlyList<ToastMessage> ActiveToasts { get; }
    IReadOnlyList<NotificationEntry> Notifications { get; }
    int UnreadCount { get; }
    void Show(string title, ToastLevel level = ToastLevel.Info, string? detail = null);
    void Dismiss(string toastId);
    void MarkAllRead();
    void ClearNotifications();
    event Action? OnChanged;
}
