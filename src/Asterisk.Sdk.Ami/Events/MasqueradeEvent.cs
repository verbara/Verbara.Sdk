using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Masquerade")]
public sealed class MasqueradeEvent : ManagerEvent
{
    public string? Clone { get; set; }
    public int? CloneState { get; set; }
    public string? CloneStateDesc { get; set; }
    public string? Original { get; set; }
    public int? OriginalState { get; set; }
    public string? OriginalStateDesc { get; set; }
}

