using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeSetSingleVideoSrc")]
public sealed class ConfbridgeSetSingleVideoSrcAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

