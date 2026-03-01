namespace Asterisk.NetAot.Pbx.Models;

/// <summary>Represents a call in the PBX activity system.</summary>
public interface ICall
{
    string Id { get; }
    CallDirection Direction { get; }
    CallState State { get; }
    IReadOnlyList<IPbxChannel> Channels { get; }
}

/// <summary>Call direction.</summary>
public enum CallDirection { Inbound, Outbound }

/// <summary>Call state.</summary>
public enum CallState { New, Ringing, Answered, OnHold, Transferred, Parked, Completed, Failed }

/// <summary>PBX channel abstraction.</summary>
public interface IPbxChannel
{
    string Name { get; }
    string UniqueId { get; }
    EndPoint? EndPoint { get; }
}

/// <summary>A telephony endpoint (e.g., SIP/2000, PJSIP/agent01).</summary>
public sealed record EndPoint(TechType Tech, string Resource)
{
    public override string ToString() => $"{Tech}/{Resource}";
}

/// <summary>Channel technology type.</summary>
public enum TechType { SIP, PJSIP, IAX2, DAHDI, Local, Agent }
