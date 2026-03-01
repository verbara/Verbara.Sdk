using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ContactListComplete")]
public sealed class ContactListComplete : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

