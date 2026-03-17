namespace PbxAdmin.Models;

public sealed record ConferenceConfig
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public int MaxMembers { get; set; }
    public string? Pin { get; set; }
    public string? AdminPin { get; set; }
    public bool Record { get; set; }
    public string MusicOnHold { get; set; } = "default";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
