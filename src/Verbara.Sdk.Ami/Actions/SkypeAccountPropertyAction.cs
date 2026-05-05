using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SkypeAccountProperty")]
public sealed class SkypeAccountPropertyAction : ManagerAction
{
    public string? User { get; set; }
}

