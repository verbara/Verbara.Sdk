using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ShowDialplanComplete")]
public sealed class ShowDialplanCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public int? ListItems { get; set; }
    public int? ListExtensions { get; set; }
    public int? ListPriorities { get; set; }
    public int? ListContexts { get; set; }
}

