using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PresenceState")]
public sealed class PresenceStateAction : ManagerAction
{
    public string? Provider { get; set; }
}
