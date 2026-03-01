using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ami.Events.Base;

/// <summary>Base class for bridge-related events.</summary>
public class BridgeEventBase : ManagerEvent
{
    public string? BridgeUniqueid { get; set; }
    public string? BridgeType { get; set; }
    public string? BridgeTechnology { get; set; }
    public string? BridgeCreator { get; set; }
    public string? BridgeName { get; set; }
    public string? BridgeNumChannels { get; set; }
    public string? BridgeVideoSourceMode { get; set; }
}
