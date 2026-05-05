using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Atxfer")]
public sealed class AtxferAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
    public int? Priority { get; set; }
}

