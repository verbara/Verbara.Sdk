using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Filter")]
public sealed class FilterAction : ManagerAction
{
    public string? Operation { get; set; }
    public string? Filter { get; set; }
}

