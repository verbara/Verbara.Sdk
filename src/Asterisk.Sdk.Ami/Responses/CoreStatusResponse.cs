using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("CoreStatus")]
public sealed class CoreStatusResponse : ManagerResponse
{
    public string? CoreReloadTime { get; set; }
    public string? CoreReloadDate { get; set; }
    public string? CoreStartupDate { get; set; }
    public string? CoreStartupTime { get; set; }
    public int? CoreCurrentCalls { get; set; }
}

