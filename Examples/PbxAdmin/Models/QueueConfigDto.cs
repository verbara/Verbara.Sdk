namespace PbxAdmin.Models;

public sealed record QueueConfigDto
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Strategy { get; set; } = "ringall";
    public int Timeout { get; set; } = 15;
    public int Retry { get; set; } = 5;
    public int MaxLen { get; set; }
    public int WrapUpTime { get; set; }
    public int ServiceLevel { get; set; } = 60;
    public string MusicOnHold { get; set; } = "default";
    public int Weight { get; set; }
    public string JoinEmpty { get; set; } = "yes";
    public string LeaveWhenEmpty { get; set; } = "no";
    public string RingInUse { get; set; } = "no";
    public int AnnounceFrequency { get; set; }
    public string AnnounceHoldTime { get; set; } = "no";
    public string AnnouncePosition { get; set; } = "no";
    public string? PeriodicAnnounce { get; set; }
    public int PeriodicAnnounceFrequency { get; set; }
    public string? QueueYouAreNext { get; set; }
    public string? QueueThereAre { get; set; }
    public string? QueueCallsWaiting { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }

    public List<QueueMemberConfigDto> Members { get; set; } = [];
}
