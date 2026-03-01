using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("UpdateConfig")]
public sealed class UpdateConfigAction : ManagerAction
{
    public string? SrcFilename { get; set; }
    public string? DstFilename { get; set; }
    public string? Reload { get; set; }
}

