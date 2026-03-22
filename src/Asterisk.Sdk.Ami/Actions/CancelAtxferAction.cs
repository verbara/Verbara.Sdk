using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("CancelAtxfer")]
public sealed class CancelAtxferAction : ManagerAction
{
    public string? Channel { get; set; }
}
