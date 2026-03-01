using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueMemberPause")]
public sealed class QueueMemberPauseEvent : ManagerEvent
{
    public string? Membership { get; set; }
    public long? Lastcall { get; set; }
    public long? Lastpause { get; set; }
    public int? CallsTaken { get; set; }
    public int? Penalty { get; set; }
    public int? Status { get; set; }
    public bool? Ringinuse { get; set; }
    public string? StateInterface { get; set; }
    public int? Incall { get; set; }
    public string? Pausedreason { get; set; }
    public int? LoginTime { get; set; }
    public int? WrapupTime { get; set; }
}

