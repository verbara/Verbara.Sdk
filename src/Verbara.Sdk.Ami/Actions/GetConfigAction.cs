using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("GetConfig")]
public sealed class GetConfigAction : ManagerAction
{
    public string? Filename { get; set; }
}

