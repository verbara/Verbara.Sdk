namespace PbxAdmin.Models;

public sealed class CosPatternGroup
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string[] Patterns { get; set; } = [];
    public string? CountryCode { get; set; }
    public bool IsBuiltIn { get; set; }
    public DateTime UpdatedAt { get; set; }
}
