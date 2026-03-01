namespace Asterisk.Sdk.Activities.Models;

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

    /// <summary>Parse "TECH/resource" string into an EndPoint.</summary>
    public static EndPoint? Parse(string channelString)
    {
        var slashIdx = channelString.IndexOf('/');
        if (slashIdx <= 0) return null;

        var tech = channelString[..slashIdx];
        var resource = channelString[(slashIdx + 1)..];

        // Strip channel suffix (e.g., "2000-00000001" -> "2000")
        var dashIdx = resource.IndexOf('-');
        if (dashIdx > 0) resource = resource[..dashIdx];

        if (!Enum.TryParse<TechType>(tech, ignoreCase: true, out var techType))
            techType = TechType.SIP;

        return new EndPoint(techType, resource);
    }
}

/// <summary>Channel technology type.</summary>
public enum TechType { SIP, PJSIP, IAX2, DAHDI, Local, Agent }

/// <summary>DTMF tone.</summary>
public enum DtmfTone
{
    Zero = '0', One = '1', Two = '2', Three = '3', Four = '4',
    Five = '5', Six = '6', Seven = '7', Eight = '8', Nine = '9',
    Star = '*', Pound = '#',
    A = 'A', B = 'B', C = 'C', D = 'D'
}

/// <summary>A phone number with optional display name.</summary>
public sealed record PhoneNumber(string Number, string? Name = null)
{
    public override string ToString() => Name is not null ? $"\"{Name}\" <{Number}>" : Number;
}

/// <summary>Caller ID with number and name.</summary>
public sealed record CallerId(string Number, string? Name = null);

/// <summary>A dialplan extension reference.</summary>
public sealed record DialPlanExtension(string Context, string Extension, int Priority = 1);
