namespace DashboardExample.Services;

public interface IQueueViewManager
{
    Task EnsureViewsExistAsync(string serverId, CancellationToken ct = default);
}
