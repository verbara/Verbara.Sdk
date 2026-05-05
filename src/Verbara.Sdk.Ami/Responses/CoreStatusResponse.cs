using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("CoreStatus")]
public sealed class CoreStatusResponse : ManagerResponse
{
    public string? CoreReloadTime { get; set; }
    public string? CoreReloadDate { get; set; }
    public string? CoreStartupDate { get; set; }
    public string? CoreStartupTime { get; set; }
    public int? CoreCurrentCalls { get; set; }
}

