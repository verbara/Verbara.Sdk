namespace PbxAdmin.Services;

/// <summary>
/// Circuit-scoped service that holds the currently selected Asterisk server ID.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Select matches the concrete class API")]
public interface ISelectedServerService
{
    string? SelectedServerId { get; }
    bool HasSelection { get; }
    event Action? OnChanged;
    void Select(string serverId);
    IEnumerable<KeyValuePair<string, AsteriskMonitorService.ServerEntry>> GetServers();
    AsteriskMonitorService.ServerEntry? GetSelectedServer();
}
