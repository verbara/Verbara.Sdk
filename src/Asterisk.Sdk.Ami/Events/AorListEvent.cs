using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AorList")]
public sealed class AorListEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
    public string? Contacts { get; set; }
}
