using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("FaxStatus")]
public sealed class FaxStatusEvent : FaxEventBase
{
    public string? OperatingMode { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public double? CallDuration { get; set; }
    public string? EcmMode { get; set; }
    public int? DataRate { get; set; }
    public string? ImageResolution { get; set; }
    public string? ImageEncoding { get; set; }
    public string? PageSize { get; set; }
    public int? DocumentNumber { get; set; }
    public int? PageNumber { get; set; }
    public string? FileName { get; set; }
    public int? TxPages { get; set; }
    public int? TxBytes { get; set; }
    public int? TotalTxLines { get; set; }
    public int? RxPages { get; set; }
    public int? RxBytes { get; set; }
    public int? TotalRxLines { get; set; }
    public int? TotalBadLines { get; set; }
    public int? DisDcsDtcCtcCount { get; set; }
    public int? CfrCount { get; set; }
    public int? FttCount { get; set; }
    public int? McfCount { get; set; }
    public int? PprCount { get; set; }
    public int? RtnCount { get; set; }
    public int? DcnCount { get; set; }
    public string? RemoteStationId { get; set; }
    public string? LocalStationId { get; set; }
    public string? CallerId { get; set; }
    public string? Status { get; set; }
    public string? Operation { get; set; }
    public string? AccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

