using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events.Base;

/// <summary>
/// Base class of the parking events (<c>ParkedCall</c>, <c>UnParkedCall</c>, <c>ParkedCallTimeOut</c>,
/// <c>ParkedCallGiveUp</c>). Asterisk prefixes every header of the parked channel with <c>Parkee</c> and sends no
/// <c>Channel</c>; the inherited channel members, <see cref="ManagerEvent.UniqueId"/> included, are read from those
/// prefixed headers, so <see cref="ChannelEventBase.Channel"/> names the parked channel.
/// </summary>
[InheritedHeaderPrefix("Parkee")]
public abstract class ParkingEventBase : ChannelEventBase
{
    /// <summary>The space the call is parked in, from the <c>ParkingSpace</c> header. Never the extension.</summary>
    public string? ParkingSpace { get; set; }

    /// <summary>The parking lot, from the header Asterisk spells <c>Parkinglot</c>.</summary>
    [VerbaraMapping("Parkinglot")]
    public string? ParkingLot { get; set; }

    /// <summary>The seconds left before the parked call times out, from the <c>ParkingTimeout</c> header.</summary>
    public string? ParkingTimeout { get; set; }

    /// <summary>The seconds the call has been parked, from the <c>ParkingDuration</c> header.</summary>
    public string? ParkingDuration { get; set; }

    /// <summary>The dial string of the channel that parked the call, from the <c>ParkerDialString</c> header.</summary>
    public string? ParkerDialString { get; set; }
}

/// <summary>
/// Names the prefix under which the deserializer generator reads every member a class inherits from the layers above
/// it, <see cref="ManagerEvent.UniqueId"/> included (from <c>&lt;prefix&gt;Uniqueid</c>). Internal: it adds no public
/// symbol.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class InheritedHeaderPrefixAttribute(string prefix) : Attribute
{
    /// <summary>The header prefix, for example <c>Parkee</c>.</summary>
    public string Prefix { get; } = prefix;
}
