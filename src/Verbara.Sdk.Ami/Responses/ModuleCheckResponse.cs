using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("ModuleCheck")]
public sealed class ModuleCheckResponse : ManagerResponse
{
    public string? Version { get; set; }
}

