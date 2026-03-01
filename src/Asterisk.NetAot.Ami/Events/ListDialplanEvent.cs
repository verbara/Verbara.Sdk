using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ListDialplan")]
public sealed class ListDialplanEvent : ResponseEvent
{
    public string? Extension { get; set; }
    public string? ExtensionLabel { get; set; }
    public string? Application { get; set; }
    public string? AppData { get; set; }
    public string? Registrar { get; set; }
    public string? IncludeContext { get; set; }
    public string? Switch { get; set; }
    public string? IgnorePattern { get; set; }
}

