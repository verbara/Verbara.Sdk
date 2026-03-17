namespace PbxAdmin.Models;

public sealed class IvrMenuViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Greeting { get; set; }
    public int ItemCount { get; set; }
    public int MaxDepth { get; set; }
    public int SubMenuCount { get; set; }
    public bool Enabled { get; set; }
    public bool IsReferenced { get; set; }
}
