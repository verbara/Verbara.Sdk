using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ResourceListDetail")]
public sealed class ResourceListDetailEvent : ManagerEvent
{
    public string? ListName { get; set; }
    public string? Event { get; set; }
}
