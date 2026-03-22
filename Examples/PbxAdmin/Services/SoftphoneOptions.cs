namespace PbxAdmin.Services;

public sealed class SoftphoneOptions
{
    public string ExtensionPrefix { get; set; } = "webrtc";
    public int WssPort { get; set; } = 8089;
    public string DefaultCodecs { get; set; } = "opus,ulaw";
    public string Context { get; set; } = "from-internal";
}
