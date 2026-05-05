using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Link")]
[Obsolete("Removed in Asterisk 12. Use BridgeEnterEvent instead.")]
public sealed class LinkEvent : ManagerEvent
{
}

