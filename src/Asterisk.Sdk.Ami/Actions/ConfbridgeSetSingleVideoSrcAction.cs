using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeSetSingleVideoSrc")]
public sealed class ConfbridgeSetSingleVideoSrcAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

