namespace PbxAdmin.Models;

public sealed class OutboundRouteViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DialPattern { get; set; } = "";
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public List<RouteTrunk> Trunks { get; set; } = [];
    public string PrimaryTrunk { get; set; } = "";
}
