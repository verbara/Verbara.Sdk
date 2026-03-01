using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMember")]
public sealed class QueueMemberEvent : ResponseEvent
{
    public string? Queue { get; set; }
    public string? Interface { get; set; }
    public string? Location { get; set; }
    public string? Membership { get; set; }
    public int? Penalty { get; set; }
    public int? CallsTaken { get; set; }
    public long? LastCall { get; set; }
    public long? LastPause { get; set; }
    public int? Status { get; set; }
    public bool? Paused { get; set; }
    public string? Name { get; set; }
    public string? MemberName { get; set; }
    public string? Stateinterface { get; set; }
    public int? Incall { get; set; }
    public string? Pausedreason { get; set; }
    public int? Wrapuptime { get; set; }
    public int? Logintime { get; set; }
}

