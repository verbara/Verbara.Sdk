using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("BlindTransfer")]
public sealed class BlindTransferEvent : BridgeEventBase
{
    public string? TransfererUniqueId { get; set; }
    public string? TransfererConnectedLineNum { get; set; }
    public string? TransfererConnectedLineName { get; set; }
    public string? TransfererCallerIdName { get; set; }
    public string? TransfererCallerIdNum { get; set; }
    public string? TransfererChannel { get; set; }
    public string? TransfererChannelState { get; set; }
    public string? TransfererChannelStateDesc { get; set; }
    public int? TransfererPriority { get; set; }
    public string? TransfererContext { get; set; }
    public string? TransfereeUniqueId { get; set; }
    public string? TransfereeConnectedLineNum { get; set; }
    public string? TransfereeConnectedLineName { get; set; }
    public string? TransfereeCallerIdName { get; set; }
    public string? TransfereeCallerIdNum { get; set; }
    public string? TransfereeChannel { get; set; }
    public string? TransfereeChannelState { get; set; }
    public string? TransfereeChannelStateDesc { get; set; }
    public int? TransfereePriority { get; set; }
    public string? TransfereeContext { get; set; }
    public string? Extension { get; set; }
    public string? Isexternal { get; set; }
    public string? Result { get; set; }
    public string? TransfereeExten { get; set; }
    public string? TransfereeLinkedId { get; set; }
    public string? TransfererAccountCode { get; set; }
    public string? TransfererExten { get; set; }
    public string? TransfererLanguage { get; set; }
    public string? TransfererLinkedId { get; set; }
    public string? TransfereeLanguage { get; set; }
    public string? Transfereeaccountcode { get; set; }
}

