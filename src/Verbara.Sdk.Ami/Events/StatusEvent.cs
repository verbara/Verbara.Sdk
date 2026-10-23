using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Status")]
public sealed class StatusEvent : ResponseEvent
{
    public string? Channel { get; set; }

    /// <summary>The channel state as a number, from the <c>ChannelState</c> header (for example <c>6</c> for Up).</summary>
    public string? ChannelState { get; set; }

    /// <summary>The channel state as text, from the <c>ChannelStateDesc</c> header (for example <c>Up</c>).</summary>
    public string? ChannelStateDesc { get; set; }

    /// <summary>The caller ID number, from the <c>CallerIDNum</c> header (for example <c>5557777</c>).</summary>
    public string? CallerIdNum { get; set; }

    /// <summary>The caller ID name, from the <c>CallerIDName</c> header (for example <c>Front Desk</c>).</summary>
    public string? CallerIdName { get; set; }

    public string? CallerId { get; set; }
    public string? AccountCode { get; set; }
    public string? Account { get; set; }
    public string? State { get; set; }

    /// <summary>The channel's dialplan extension, from the <c>Exten</c> header (for example <c>100</c>).</summary>
    /// <remarks>
    /// Every supported Asterisk version measured (18, 20, 22 and 23, on every captured frame) sends this value
    /// under the header name <c>Exten</c>, never <c>Extension</c>, so the member is bound to <c>Exten</c>.
    /// A leg reported with an empty <c>Exten</c> (for example a dialled leg running <c>AppDial</c>) reads as an
    /// empty string, not as null.
    /// </remarks>
    [VerbaraMapping("Exten")]
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

