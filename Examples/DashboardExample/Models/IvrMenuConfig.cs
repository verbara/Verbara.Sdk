namespace DashboardExample.Models;

public sealed record IvrMenuConfig
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Greeting { get; set; }
    public int Timeout { get; set; } = 5;
    public int MaxRetries { get; set; } = 3;
    public string? InvalidDestType { get; set; }
    public string? InvalidDest { get; set; }
    public string? TimeoutDestType { get; set; }
    public string? TimeoutDest { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
    public List<IvrMenuItemConfig> Items { get; set; } = [];
}
