using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ami.Events.Base;

/// <summary>Base class for FAX events.</summary>
public class FaxEventBase : ManagerEvent
{
    public string? Channel { get; set; }
    public string? LocalStationID { get; set; }
    public string? RemoteStationID { get; set; }
}
