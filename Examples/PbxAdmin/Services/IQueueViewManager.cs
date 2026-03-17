namespace PbxAdmin.Services;

public interface IQueueViewManager
{
    Task EnsureViewsExistAsync(string serverId, CancellationToken ct = default);
}
