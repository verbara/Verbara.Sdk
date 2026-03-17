namespace PbxAdmin.Models;

public sealed record MohClass
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "files";
    public string Directory { get; set; } = "";
    public string Sort { get; set; } = "random";
    public string? CustomApplication { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
