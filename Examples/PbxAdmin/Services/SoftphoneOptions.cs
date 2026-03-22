namespace PbxAdmin.Services;

public sealed class SoftphoneOptions
{
    public string ExtensionPrefix { get; set; } = "webrtc";
    public int WssPort { get; set; } = 8089;
    public string? WssHost { get; set; }
    public bool UseTls { get; set; }
    public string DefaultCodecs { get; set; } = "opus,ulaw";
    public string Context { get; set; } = "from-internal";
}
