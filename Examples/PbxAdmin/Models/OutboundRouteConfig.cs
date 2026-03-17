namespace PbxAdmin.Models;

public sealed class OutboundRouteConfig
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DialPattern { get; set; } = "";
    public string? Prepend { get; set; }
    public string? Prefix { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
    public List<RouteTrunk> Trunks { get; set; } = [];
}
