namespace PbxAdmin.Models;

public sealed class TrunkDetailViewModel : TrunkViewModel
{
    public TrunkConfig Config { get; set; } = new();
    public string? ContactUri { get; set; }
    public string? UserAgent { get; set; }
    public int? RoundtripMs { get; set; }
}
