using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

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

