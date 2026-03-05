using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("IdentifyDetail")]
public sealed class IdentifyDetailEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
    public string? Endpoint { get; set; }
    public string? Match { get; set; }
    public string? SrvLookups { get; set; }
    public string? MatchHeader { get; set; }
}
