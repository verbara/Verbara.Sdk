using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ami.Events.Base;

/// <summary>Base class for ConfBridge conference events.</summary>
public class ConfbridgeEventBase : ManagerEvent
{
    public string? Conference { get; set; }
    public string? BridgeUniqueid { get; set; }
    public string? Channel { get; set; }
}
