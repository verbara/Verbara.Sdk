using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("NewExten")]
public sealed class NewExtenEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? Application { get; set; }
    public string? AppData { get; set; }
    public string? Channel { get; set; }
    public string? Extension { get; set; }
    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }
}

