using System.ComponentModel.DataAnnotations;

namespace Asterisk.Sdk.Sessions.Manager;

public sealed class SessionOptions
{
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DialingTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan RingingTimeout { get; set; } = TimeSpan.FromSeconds(120);

    [Range(1, int.MaxValue)]
    public int MaxCompletedSessions { get; set; } = 1000;

    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan QueueMetricsWindow { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SlaThreshold { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan WrapUpDuration { get; set; } = TimeSpan.FromSeconds(30);

    [Required]
    public string[] InboundContextPatterns { get; set; } = ["from-trunk", "from-pstn", "from-external"];

    [Required]
    public string[] OutboundContextPatterns { get; set; } = ["from-internal", "from-sip", "from-users"];
}
