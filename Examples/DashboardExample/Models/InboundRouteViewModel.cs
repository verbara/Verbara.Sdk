namespace DashboardExample.Models;

public sealed class InboundRouteViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DidPattern { get; set; } = "";
    public string DestinationType { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? DestinationLabel { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; }
}
