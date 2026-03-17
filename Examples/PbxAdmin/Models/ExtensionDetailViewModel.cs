namespace PbxAdmin.Models;

public sealed class ExtensionDetailViewModel : ExtensionViewModel
{
    public string? Context { get; set; }
    public string? CallGroup { get; set; }
    public string? PickupGroup { get; set; }
    public string? Codecs { get; set; }
    public string? ContactUri { get; set; }
    public int? RoundtripMs { get; set; }
    public int? VoicemailMessages { get; set; }
    public string? CfBusy { get; set; }
    public string? CfNoAnswer { get; set; }
    public int CfNoAnswerTimeout { get; set; }
    public string? VoicemailEmail { get; set; }
    public Dictionary<string, Dictionary<string, string>> RawConfig { get; set; } = [];
}
