using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 split this event into AGIExecStart and AGIExecEnd; use AgiExecStartEvent and AgiExecEndEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
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

