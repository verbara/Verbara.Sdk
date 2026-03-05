using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Link")]
[Obsolete("Removed in Asterisk 12. Use BridgeEnterEvent instead.")]
public sealed class LinkEvent : ManagerEvent
{
}

