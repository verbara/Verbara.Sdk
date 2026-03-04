namespace DashboardExample.Services;

/// <summary>
/// Circuit-scoped service that holds the currently selected Asterisk server ID.
/// One instance per Blazor circuit (browser tab).
/// </summary>
public sealed class SelectedServerService
{
    private readonly AsteriskMonitorService _monitor;

    public SelectedServerService(AsteriskMonitorService monitor)
    {
        _monitor = monitor;
    }

    public string? SelectedServerId { get; private set; }

    public bool HasSelection => SelectedServerId is not null;

    public event Action? OnChanged;

    public void Select(string serverId)
    {
        if (SelectedServerId == serverId) return;
        SelectedServerId = serverId;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Returns a single-element enumerable containing only the selected server,
    /// matching the pattern pages already use with GetServers().
    /// </summary>
    public IEnumerable<KeyValuePair<string, AsteriskMonitorService.ServerEntry>> GetServers()
    {
        var entry = SelectedServerId is not null ? _monitor.GetServer(SelectedServerId) : null;
        if (entry is not null)
            return [new KeyValuePair<string, AsteriskMonitorService.ServerEntry>(SelectedServerId!, entry)];

        return [];
    }

    public AsteriskMonitorService.ServerEntry? GetSelectedServer() =>
        SelectedServerId is not null ? _monitor.GetServer(SelectedServerId) : null;
}
