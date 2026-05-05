using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SendText")]
public sealed class SendTextAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Message { get; set; }
}

