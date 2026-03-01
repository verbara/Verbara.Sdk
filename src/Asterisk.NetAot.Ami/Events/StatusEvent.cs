using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Status")]
public sealed class StatusEvent : ResponseEvent
{
    public string? Channel { get; set; }
    public string? CallerId { get; set; }
    public string? AccountCode { get; set; }
    public string? Account { get; set; }
    public string? State { get; set; }
    public string? Extension { get; set; }
    public int? Seconds { get; set; }
    public string? BridgedChannel { get; set; }
    public string? Link { get; set; }
    public string? BridgedUniqueId { get; set; }
    public string? LinkedId { get; set; }
    public string? Data { get; set; }
    public string? ReadFormat { get; set; }
    public string? WriteFormat { get; set; }
    public string? Type { get; set; }
    public string? EffectiveConnectedLineName { get; set; }
    public string? EffectiveConnectedLineNum { get; set; }
    public string? Application { get; set; }
    public string? CallGroup { get; set; }
    public string? NativeFormats { get; set; }
    public string? PickupGroup { get; set; }
    public string? TimeToHangup { get; set; }
    public string? Dnid { get; set; }
    public string? Writetrans { get; set; }
    public string? BridgeId { get; set; }
    public string? Readtrans { get; set; }
    public string? Language { get; set; }
}

