using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueReload")]
public sealed class QueueReloadAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Members { get; set; }
    public string? Rules { get; set; }
    public string? Parameters { get; set; }
}
