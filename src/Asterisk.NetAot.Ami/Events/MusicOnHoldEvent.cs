using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MusicOnHold")]
public sealed class MusicOnHoldEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? ClassName { get; set; }
    public string? State { get; set; }
    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }
    public string? Language { get; set; }
}

