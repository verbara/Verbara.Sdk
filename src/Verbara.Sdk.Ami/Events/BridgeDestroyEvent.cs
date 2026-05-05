using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeDestroy")]
public sealed class BridgeDestroyEvent : BridgeEventBase
{
}

