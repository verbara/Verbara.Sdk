namespace PbxAdmin.Services;

public interface IWebRtcExtensionProvider
{
    /// <summary>
    /// Provisions a WebRTC PJSIP extension. Finds the next available numeric extension
    /// within the server's configured range, creates it, and returns credentials.
    /// </summary>
    Task<WebRtcCredentials> ProvisionAsync(string serverId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string serverId, string extensionId, CancellationToken ct = default);
}

public sealed record WebRtcCredentials(string Extension, string Password, string WssUrl);
