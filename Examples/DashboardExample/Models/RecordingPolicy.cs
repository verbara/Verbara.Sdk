namespace DashboardExample.Models;

public enum RecordingMode { Always, OnDemand, Never }

public sealed record RecordingPolicy
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public RecordingMode Mode { get; set; } = RecordingMode.Always;
    public string Format { get; set; } = "wav";
    public string StoragePath { get; set; } = "/var/spool/asterisk/monitor/";
    public int RetentionDays { get; set; }
    public string? MixMonitorOptions { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PolicyTarget> Targets { get; set; } = [];
}

public sealed record PolicyTarget
{
    public int Id { get; set; }
    public int PolicyId { get; set; }
    public string TargetType { get; set; } = "";
    public string TargetValue { get; set; } = "";
}
