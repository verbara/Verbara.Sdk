using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("RegistrationsComplete")]
public sealed class RegistrationsCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

