namespace DashboardExample.Models;

public class TrunkViewModel
{
    public string Name { get; set; } = "";
    public TrunkTechnology Technology { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public TrunkStatus Status { get; set; }
    public int ActiveCalls { get; set; }
    public int MaxChannels { get; set; }
    public string Codecs { get; set; } = "";
}
