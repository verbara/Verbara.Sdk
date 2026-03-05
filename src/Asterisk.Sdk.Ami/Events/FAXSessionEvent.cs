using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FAXSession")]
public sealed class FAXSessionEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? SessionNumber { get; set; }
    public string? Operation { get; set; }
    public string? State { get; set; }
}
