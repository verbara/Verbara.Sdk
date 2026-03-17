namespace PbxAdmin.Models;

public sealed class InboundRouteConfig
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DidPattern { get; set; } = "";
    public string DestinationType { get; set; } = "";
    public string Destination { get; set; } = "";
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
}
