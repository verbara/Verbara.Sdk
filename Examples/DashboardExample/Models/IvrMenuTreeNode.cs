namespace DashboardExample.Models;

public sealed class IvrMenuTreeNode
{
    public int MenuId { get; set; }
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Digit { get; set; }
    public List<IvrMenuTreeNode> Children { get; set; } = [];
}
