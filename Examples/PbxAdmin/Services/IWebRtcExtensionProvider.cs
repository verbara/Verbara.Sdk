namespace PbxAdmin.Services;

public interface IWebRtcExtensionProvider
{
    Task<WebRtcCredentials> ProvisionAsync(string serverId, string username, CancellationToken ct = default);
    Task<bool> ExistsAsync(string serverId, string extensionId, CancellationToken ct = default);
}

public sealed record WebRtcCredentials(string Extension, string Password, string WssUrl);
