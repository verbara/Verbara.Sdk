using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ShowDialplanComplete")]
public sealed class ShowDialplanCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public int? ListItems { get; set; }
    public int? ListExtensions { get; set; }
    public int? ListPriorities { get; set; }
    public int? ListContexts { get; set; }
}

