using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("ModuleCheck")]
public sealed class ModuleCheckResponse : ManagerResponse
{
    public string? Version { get; set; }
}

