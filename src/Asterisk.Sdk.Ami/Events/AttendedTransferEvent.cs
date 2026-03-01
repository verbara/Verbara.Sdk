using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AttendedTransfer")]
public sealed class AttendedTransferEvent : BridgeEventBase
{
    public string? OrigTransfererChannel { get; set; }
    public string? OrigTransfererChannelState { get; set; }
    public string? OrigTransfererChannelStateDesc { get; set; }
    public string? OrigTransfererCallerIDNum { get; set; }
    public string? OrigTransfererCallerIDName { get; set; }
    public string? OrigTransfererConnectedLineNum { get; set; }
    public string? OrigTransfererConnectedLineName { get; set; }
    public string? OrigTransfererAccountCode { get; set; }
    public string? OrigTransfererContext { get; set; }
    public string? OrigTransfererExten { get; set; }
    public string? OrigTransfererPriority { get; set; }
    public string? OrigTransfererUniqueid { get; set; }
    public string? OrigBridgeUniqueid { get; set; }
    public string? OrigBridgeType { get; set; }
    public string? OrigBridgeTechnology { get; set; }
    public string? OrigBridgeCreator { get; set; }
    public string? OrigBridgeName { get; set; }
    public string? OrigBridgeNumChannels { get; set; }
    public string? SecondTransfererChannel { get; set; }
    public string? SecondTransfererChannelState { get; set; }
    public string? SecondTransfererChannelStateDesc { get; set; }
    public string? SecondTransfererCallerIDNum { get; set; }
    public string? SecondTransfererCallerIDName { get; set; }
    public string? SecondTransfererConnectedLineNum { get; set; }
    public string? SecondTransfererConnectedLineName { get; set; }
    public string? SecondTransfererAccountCode { get; set; }
    public string? SecondTransfererContext { get; set; }
    public string? SecondTransfererExten { get; set; }
    public string? SecondTransfererPriority { get; set; }
    public string? SecondTransfererUniqueid { get; set; }
    public string? SecondBridgeUniqueid { get; set; }
    public string? SecondBridgeType { get; set; }
    public string? SecondBridgeTechnology { get; set; }
    public string? SecondBridgeCreator { get; set; }
    public string? SecondBridgeName { get; set; }
    public string? SecondBridgeNumChannels { get; set; }
    public string? DestType { get; set; }
    public string? DestBridgeUniqueid { get; set; }
    public string? DestApp { get; set; }
    public string? LocalOneChannel { get; set; }
    public string? LocalOneChannelState { get; set; }
    public string? LocalOneChannelStateDesc { get; set; }
    public string? LocalOneCallerIDNum { get; set; }
    public string? LocalOneCallerIDName { get; set; }
    public string? LocalOneConnectedLineNum { get; set; }
    public string? LocalOneConnectedLineName { get; set; }
    public string? LocalOneAccountCode { get; set; }
    public string? LocalOneContext { get; set; }
    public string? LocalOneExten { get; set; }
    public string? LocalOnePriority { get; set; }
    public string? LocalOneUniqueid { get; set; }
    public string? LocalTwoChannel { get; set; }
    public string? LocalTwoChannelState { get; set; }
    public string? LocalTwoChannelStateDesc { get; set; }
    public string? LocalTwoCallerIDNum { get; set; }
    public string? LocalTwoCallerIDName { get; set; }
    public string? LocalTwoConnectedLineNum { get; set; }
    public string? LocalTwoConnectedLineName { get; set; }
    public string? LocalTwoAccountCode { get; set; }
    public string? LocalTwoContext { get; set; }
    public string? LocalTwoExten { get; set; }
    public string? LocalTwoPriority { get; set; }
    public string? LocalTwoUniqueid { get; set; }
    public string? DestTransfererChannel { get; set; }
    public string? TransfereeChannel { get; set; }
    public string? TransfereeChannelState { get; set; }
    public string? TransfereeChannelStateDesc { get; set; }
    public string? TransfereeCallerIDNum { get; set; }
    public string? TransfereeCallerIDName { get; set; }
    public string? TransfereeConnectedLineNum { get; set; }
    public string? TransfereeConnectedLineName { get; set; }
    public string? TransfereeAccountCode { get; set; }
    public string? TransfereeContext { get; set; }
    public string? TransfereeExten { get; set; }
    public string? TransfereePriority { get; set; }
    public string? TransfereeUniqueid { get; set; }
    public string? TransfereeLinkedId { get; set; }
    public string? TransfereeLanguage { get; set; }
    public string? OrigTransfererLinkedId { get; set; }
    public string? SecondTransfererLanguage { get; set; }
    public string? Isexternal { get; set; }
    public string? Result { get; set; }
    public string? SecondTransfererLinkedId { get; set; }
    public string? OrigTransfererLanguage { get; set; }
    public string? TransferTargetUniqueID { get; set; }
    public string? TransferTargetCallerIDName { get; set; }
    public string? SecondBridgeVideoSourceMode { get; set; }
    public string? TransferTargetLinkedID { get; set; }
    public string? TransferTargetPriority { get; set; }
    public string? TransferTargetCallerIDNum { get; set; }
    public string? OrigBridgeVideoSourceMode { get; set; }
    public string? TransferTargetConnectedLineNum { get; set; }
    public string? TransferTargetChannel { get; set; }
    public string? TransferTargetContext { get; set; }
    public string? TransferTargetConnectedLineName { get; set; }
    public string? TransferTargetExten { get; set; }
    public string? TransferTargetChannelState { get; set; }
    public string? TransferTargetLanguage { get; set; }
    public string? TransferTargetAccountCode { get; set; }
    public string? TransferTargetChannelStateDesc { get; set; }
}

