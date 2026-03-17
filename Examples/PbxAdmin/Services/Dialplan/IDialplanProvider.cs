namespace PbxAdmin.Services.Dialplan;

public interface IDialplanProvider
{
    Task<bool> GenerateDialplanAsync(string serverId, DialplanData data, CancellationToken ct = default);
    Task<bool> ReloadAsync(string serverId, CancellationToken ct = default);
}
