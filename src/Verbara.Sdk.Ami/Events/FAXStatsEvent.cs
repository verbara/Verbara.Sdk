using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("FAXStats")]
public sealed class FAXStatsEvent : ManagerEvent
{
    public string? CurrentSessions { get; set; }
    public string? ReservedSessions { get; set; }
    public string? TransmitAttempts { get; set; }
    public string? ReceiveAttempts { get; set; }
    public string? CompletedFAXes { get; set; }
    public string? FailedFAXes { get; set; }
}
