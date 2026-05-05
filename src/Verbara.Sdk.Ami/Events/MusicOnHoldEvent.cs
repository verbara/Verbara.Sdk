using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MusicOnHold")]
public sealed class MusicOnHoldEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? ClassName { get; set; }
    public string? State { get; set; }
    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }
    public string? Language { get; set; }
}

