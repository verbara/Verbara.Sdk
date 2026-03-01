using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("GetConfig")]
public sealed class GetConfigAction : ManagerAction
{
    public string? Filename { get; set; }
}

