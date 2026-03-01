using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("CoreStatus")]
public sealed class CoreStatusResponse : ManagerResponse
{
    public string? CoreReloadTime { get; set; }
    public string? CoreReloadDate { get; set; }
    public string? CoreStartupDate { get; set; }
    public string? CoreStartupTime { get; set; }
    public int? CoreCurrentCalls { get; set; }
}

