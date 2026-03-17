namespace DashboardExample.Models;

public sealed record IvrMenuItemConfig
{
    public int Id { get; set; }
    public int MenuId { get; set; }
    public string Digit { get; set; } = "";
    public string? Label { get; set; }
    public string DestType { get; set; } = "";
    public string DestTarget { get; set; } = "";
    public string? Trunk { get; set; }
}
