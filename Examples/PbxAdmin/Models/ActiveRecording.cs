namespace PbxAdmin.Models;

public sealed record ActiveRecording
{
    public string Channel { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public int? PolicyId { get; set; }
}
