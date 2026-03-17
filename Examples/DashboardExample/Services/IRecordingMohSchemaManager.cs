namespace DashboardExample.Services;

public interface IRecordingMohSchemaManager
{
    bool IsAvailable { get; }
    Task EnsureSchemaAsync(CancellationToken ct = default);
}
