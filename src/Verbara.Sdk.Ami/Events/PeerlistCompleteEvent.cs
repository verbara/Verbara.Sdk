using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("PeerlistComplete")]
public sealed class PeerlistCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

