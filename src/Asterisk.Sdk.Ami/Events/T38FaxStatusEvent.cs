using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("T38FaxStatus")]
public sealed class T38FaxStatusEvent : FaxEventBase
{
    public string? MaxLag { get; set; }
    public string? TotalLag { get; set; }
    public string? AverageLag { get; set; }
    public int? TotalEvents { get; set; }
    public string? T38SessionDuration { get; set; }
    public int? T38PacketsSent { get; set; }
    public int? T38OctetsSent { get; set; }
    public string? AverageTxDataRate { get; set; }
    public int? T38PacketsReceived { get; set; }
    public int? T38OctetsReceived { get; set; }
    public string? AverageRxDataRate { get; set; }
    public int? JitterBufferOverflows { get; set; }
    public int? MinimumJitterSpace { get; set; }
    public int? UnrecoverablePackets { get; set; }
    public int? TotalLagInMilliSeconds { get; set; }
    public int? MaxLagInMilliSeconds { get; set; }
    public double? T38SessionDurationInSeconds { get; set; }
    public double? AverageLagInMilliSeconds { get; set; }
    public int? AverageTxDataRateInBps { get; set; }
    public int? AverageRxDataRateInBps { get; set; }
    public string? AccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

