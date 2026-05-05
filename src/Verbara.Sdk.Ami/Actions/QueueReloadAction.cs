using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueReload")]
public sealed class QueueReloadAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Members { get; set; }
    public string? Rules { get; set; }
    public string? Parameters { get; set; }
}
