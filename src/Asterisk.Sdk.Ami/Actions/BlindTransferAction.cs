using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BlindTransfer")]
public sealed class BlindTransferAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
}
