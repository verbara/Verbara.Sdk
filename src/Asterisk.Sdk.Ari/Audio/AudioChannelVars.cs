namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// Constants for chan_websocket, chan_audiosocket, and ExternalMedia channel variables.
/// Use with IAriChannelsResource.GetVariableAsync/SetVariableAsync.
/// </summary>
public static class AudioChannelVars
{
    // Channel function variables (set by Asterisk)
    public const string PeerIp = "CHANNEL(peerip)";
    public const string WriteFormat = "CHANNEL(writeformat)";
    public const string ReadFormat = "CHANNEL(readformat)";

    // ExternalMedia-specific
    public const string ExternalMediaProtocol = "EXTERNALMEDIA_PROTOCOL";
    public const string ExternalMediaAddress = "EXTERNALMEDIA_ADDRESS";

    // WebSocket-specific (chan_websocket)
    public const string WebSocketProtocol = "WEBSOCKET_PROTOCOL";
    public const string WebSocketGuid = "WEBSOCKET_GUID";
    public const string WebSocketUri = "WEBSOCKET_URI";

    // Audio format constants
    public const string Slin16 = "slin16";
    public const string Slin8 = "slin";
    public const string Slin48 = "slin48";
    public const string Ulaw = "ulaw";
    public const string Alaw = "alaw";
    public const string Opus = "opus";
    public const string G729 = "g729";
}
