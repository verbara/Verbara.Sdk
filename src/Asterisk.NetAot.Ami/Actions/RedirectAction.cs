using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Redirect")]
public sealed class RedirectAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? ExtraChannel { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
    public int? Priority { get; set; }
    public string? ExtraContext { get; set; }
    public string? ExtraExten { get; set; }
    public int? ExtraPriority { get; set; }
}

