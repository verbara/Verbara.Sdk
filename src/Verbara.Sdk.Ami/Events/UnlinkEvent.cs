using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Unlink")]
[Obsolete("Removed in Asterisk 12. Use BridgeLeaveEvent instead.")]
public sealed class UnlinkEvent : ManagerEvent
{
}

