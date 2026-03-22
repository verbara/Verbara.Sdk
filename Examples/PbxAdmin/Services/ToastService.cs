using PbxAdmin.Models;

namespace PbxAdmin.Services;

public sealed class ToastService : IToastService, IDisposable
{
    private readonly List<ToastMessage> _toasts = [];
    private readonly LinkedList<NotificationEntry> _notifications = new();
    private readonly Lock _lock = new();
    private const int MaxNotifications = 100;
    private const int MaxVisibleToasts = 5;

    public IReadOnlyList<ToastMessage> ActiveToasts { get { lock (_lock) return _toasts.ToList(); } }
    public IReadOnlyList<NotificationEntry> Notifications { get { lock (_lock) return _notifications.ToList(); } }
    public int UnreadCount { get { lock (_lock) return _notifications.Count(n => !n.IsRead); } }
    public event Action? OnChanged;

    public void Show(string title, ToastLevel level = ToastLevel.Info, string? detail = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var autoDismiss = level is not ToastLevel.Error;
        var toast = new ToastMessage(id, level, title, detail, DateTimeOffset.UtcNow, autoDismiss);

        lock (_lock)
        {
            _toasts.Add(toast);
            if (_toasts.Count > MaxVisibleToasts)
                _toasts.RemoveAt(0);

            _notifications.AddFirst(new NotificationEntry
            {
                Id = id, Level = level, Title = title,
                Detail = detail, Timestamp = DateTimeOffset.UtcNow
            });
            while (_notifications.Count > MaxNotifications)
                _notifications.RemoveLast();
        }
        OnChanged?.Invoke();

        if (autoDismiss)
        {
            var dismissMs = level is ToastLevel.Warning ? 8000 : 5000;
            _ = Task.Delay(dismissMs).ContinueWith(_ => Dismiss(id));
        }
    }

    public void Dismiss(string toastId)
    {
        lock (_lock)
            _toasts.RemoveAll(t => t.Id == toastId);
        OnChanged?.Invoke();
    }

    public void MarkAllRead()
    {
        lock (_lock)
            foreach (var n in _notifications) n.IsRead = true;
        OnChanged?.Invoke();
    }

    public void ClearNotifications()
    {
        lock (_lock)
            _notifications.Clear();
        OnChanged?.Invoke();
    }

    public void Dispose() { }
}
