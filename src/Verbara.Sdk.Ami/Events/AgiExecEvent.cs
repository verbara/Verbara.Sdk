using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgiExec")]
public sealed class AgiExecEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? SubEvent { get; set; }
    public string? CommandId { get; set; }
    public string? Command { get; set; }
    public int? ResultCode { get; set; }
    public string? Result { get; set; }
    public string? AccountCode { get; set; }
}

