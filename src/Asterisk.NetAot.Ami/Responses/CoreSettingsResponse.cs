using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("CoreSettings")]
public sealed class CoreSettingsResponse : ManagerResponse
{
    public string? AmiVersion { get; set; }
    public string? AsteriskVersion { get; set; }
    public string? SystemName { get; set; }
    public int? CoreMaxCalls { get; set; }
    public double? CoreMaxLoadAvg { get; set; }
    public string? CoreRunUser { get; set; }
    public string? CoreRunGroup { get; set; }
    public int? CoreMaxFilehandles { get; set; }
}

