namespace PbxAdmin.Models;

public sealed class ExtensionTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public ExtensionConfig Config { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
