using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PresenceState")]
public sealed class PresenceStateAction : ManagerAction
{
    public string? Provider { get; set; }
}
