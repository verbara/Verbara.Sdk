using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Unlink")]
[Obsolete("Removed in Asterisk 12. Use BridgeLeaveEvent instead.")]
public sealed class UnlinkEvent : ManagerEvent
{
}

