using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("GetConfig")]
public sealed class GetConfigAction : ManagerAction
{
    public string? Filename { get; set; }
}

