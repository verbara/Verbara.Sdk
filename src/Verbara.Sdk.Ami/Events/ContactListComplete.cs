using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ContactListComplete")]
public sealed class ContactListComplete : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

