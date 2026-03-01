using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("FaxDocumentStatus")]
public sealed class FaxDocumentStatusEvent : FaxEventBase
{
    public int? DocumentNumber { get; set; }
    public int? LastError { get; set; }
    public int? PageCount { get; set; }
    public int? StartPage { get; set; }
    public int? LastPageProcessed { get; set; }
    public int? RetransmitCount { get; set; }
    public int? TransferPels { get; set; }
    public int? TransferRate { get; set; }
    public string? TransferDuration { get; set; }
    public int? BadLineCount { get; set; }
    public string? ProcessedStatus { get; set; }
    public string? DocumentTime { get; set; }
    public string? LocalSid { get; set; }
    public string? LocalDis { get; set; }
    public string? RemoteSid { get; set; }
    public string? RemoteDis { get; set; }
    public string? AccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

