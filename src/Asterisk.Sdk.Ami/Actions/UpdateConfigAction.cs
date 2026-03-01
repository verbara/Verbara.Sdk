using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("UpdateConfig")]
public sealed class UpdateConfigAction : ManagerAction
{
    public string? SrcFilename { get; set; }
    public string? DstFilename { get; set; }
    public string? Reload { get; set; }
}

