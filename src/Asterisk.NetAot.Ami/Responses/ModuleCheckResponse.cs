using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("ModuleCheck")]
public sealed class ModuleCheckResponse : ManagerResponse
{
    public string? Version { get; set; }
}

