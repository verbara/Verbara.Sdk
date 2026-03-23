using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ListDialplan")]
public sealed class ListDialplanEvent : ResponseEvent
{
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public string? ExtensionLabel { get; set; }
    public string? Application { get; set; }
    public string? AppData { get; set; }
    public string? Registrar { get; set; }
    public string? IncludeContext { get; set; }
    public string? Switch { get; set; }
    public int? Priority { get; set; }
    public string? IgnorePattern { get; set; }
}

