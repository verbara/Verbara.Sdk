using PbxAdmin.Models;

namespace PbxAdmin.Services;

/// <summary>
/// Routes WebRTC provisioning requests to the correct provider based on the server's config mode.
/// Realtime servers use <see cref="RealtimeWebRtcProvider"/>; file-based servers use <see cref="FileWebRtcProvider"/>.
/// </summary>
public sealed class WebRtcProviderResolver
{
    private readonly RealtimeWebRtcProvider _realtime;
    private readonly FileWebRtcProvider _file;
    private readonly IConfigProviderResolver _configResolver;

    public WebRtcProviderResolver(
        RealtimeWebRtcProvider realtime,
        FileWebRtcProvider file,
        IConfigProviderResolver configResolver)
    {
        _realtime = realtime;
        _file = file;
        _configResolver = configResolver;
    }

    /// <summary>
    /// Returns the correct <see cref="IWebRtcExtensionProvider"/> for the given server.
    /// Realtime mode → <see cref="RealtimeWebRtcProvider"/>; File mode → <see cref="FileWebRtcProvider"/>.
    /// </summary>
    public IWebRtcExtensionProvider GetProvider(string serverId)
    {
        var mode = _configResolver.GetConfigMode(serverId);
        return mode == ConfigMode.Realtime ? _realtime : _file;
    }
}
