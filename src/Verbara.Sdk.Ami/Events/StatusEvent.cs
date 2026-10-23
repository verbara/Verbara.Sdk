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

    /// <summary>Always null: no supported Asterisk version sends a <c>CallerID</c> header on a Status frame. Read <see cref="CallerIdNum"/> (number) or <see cref="CallerIdName"/> (name).</summary>
    /// <remarks>
    /// <para>
    /// Measured on the Status replies of Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no frame carries
    /// a <c>CallerID</c> header, so the member is never assigned.
    /// It still compiles, and the generated deserializer still binds it; nothing is taken away.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can
    /// suppress this one deprecation with <c>&lt;NoWarn&gt;VSDK0002&lt;/NoWarn&gt;</c> instead of
    /// silencing every <c>CS0618</c> in their tree.
    /// </para>
    /// </remarks>
    [Obsolete(
        "No supported Asterisk version sends a CallerID header on a Status frame, so this is always null. Read CallerIdNum (number) or CallerIdName (name).",
        DiagnosticId = "VSDK0002")]
    public string? CallerId { get; set; }

    public string? AccountCode { get; set; }

    /// <summary>Always null: no supported Asterisk version sends an <c>Account</c> header on a Status frame. Read <see cref="AccountCode"/>.</summary>
    /// <remarks>
    /// <para>
    /// Measured on the Status replies of Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no frame carries
    /// an <c>Account</c> header, so the member is never assigned.
    /// It still compiles, and the generated deserializer still binds it; nothing is taken away.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can
    /// suppress this one deprecation with <c>&lt;NoWarn&gt;VSDK0002&lt;/NoWarn&gt;</c> instead of
    /// silencing every <c>CS0618</c> in their tree.
    /// </para>
    /// </remarks>
    [Obsolete(
        "No supported Asterisk version sends an Account header on a Status frame, so this is always null. Read AccountCode.",
        DiagnosticId = "VSDK0002")]
    public string? Account { get; set; }

    /// <summary>Always null: no supported Asterisk version sends a <c>State</c> header on a Status frame. Read <see cref="ChannelStateDesc"/> (text) or <see cref="ChannelState"/> (numeric).</summary>
    /// <remarks>
    /// <para>
    /// Measured on the Status replies of Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no frame carries
    /// a <c>State</c> header, so the member is never assigned.
    /// It still compiles, and the generated deserializer still binds it; nothing is taken away.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can
    /// suppress this one deprecation with <c>&lt;NoWarn&gt;VSDK0002&lt;/NoWarn&gt;</c> instead of
    /// silencing every <c>CS0618</c> in their tree.
    /// </para>
    /// </remarks>
    [Obsolete(
        "No supported Asterisk version sends a State header on a Status frame, so this is always null. Read ChannelStateDesc (text) or ChannelState (numeric).",
        DiagnosticId = "VSDK0002")]
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

    /// <summary>Always null: no supported Asterisk version sends a <c>BridgedChannel</c> header on a Status frame. Read <see cref="BridgeId"/>: a Status frame names the bridge, never the peer channel.</summary>
    /// <remarks>
    /// <para>
    /// Measured on the Status replies of Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no frame carries
    /// a <c>BridgedChannel</c> header, so the member is never assigned.
    /// It still compiles, and the generated deserializer still binds it; nothing is taken away.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can
    /// suppress this one deprecation with <c>&lt;NoWarn&gt;VSDK0002&lt;/NoWarn&gt;</c> instead of
    /// silencing every <c>CS0618</c> in their tree.
    /// </para>
    /// </remarks>
    [Obsolete(
        "No supported Asterisk version sends a BridgedChannel header on a Status frame, so this is always null. Read BridgeId: a Status frame names the bridge, never the peer channel.",
        DiagnosticId = "VSDK0002")]
    public string? BridgedChannel { get; set; }

    /// <summary>Always null: no supported Asterisk version sends a <c>Link</c> header on a Status frame. Read <see cref="BridgeId"/>: a Status frame names the bridge, never the peer channel.</summary>
    /// <remarks>
    /// <para>
    /// Measured on the Status replies of Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no frame carries
    /// a <c>Link</c> header, so the member is never assigned.
    /// It still compiles, and the generated deserializer still binds it; nothing is taken away.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can
    /// suppress this one deprecation with <c>&lt;NoWarn&gt;VSDK0002&lt;/NoWarn&gt;</c> instead of
    /// silencing every <c>CS0618</c> in their tree.
    /// </para>
    /// </remarks>
    [Obsolete(
        "No supported Asterisk version sends a Link header on a Status frame, so this is always null. Read BridgeId: a Status frame names the bridge, never the peer channel.",
        DiagnosticId = "VSDK0002")]
    public string? Link { get; set; }

    /// <summary>Always null: no supported Asterisk version sends a <c>BridgedUniqueId</c> header on a Status frame. Read <see cref="BridgeId"/>: a Status frame names the bridge, never the peer channel.</summary>
    /// <remarks>
    /// <para>
    /// Measured on the Status replies of Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no frame carries
    /// a <c>BridgedUniqueId</c> header, so the member is never assigned.
    /// It still compiles, and the generated deserializer still binds it; nothing is taken away.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can
    /// suppress this one deprecation with <c>&lt;NoWarn&gt;VSDK0002&lt;/NoWarn&gt;</c> instead of
    /// silencing every <c>CS0618</c> in their tree.
    /// </para>
    /// </remarks>
    [Obsolete(
        "No supported Asterisk version sends a BridgedUniqueId header on a Status frame, so this is always null. Read BridgeId: a Status frame names the bridge, never the peer channel.",
        DiagnosticId = "VSDK0002")]
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

