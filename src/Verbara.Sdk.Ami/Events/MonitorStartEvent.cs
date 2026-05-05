using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MonitorStart")]
[Obsolete("app_monitor removed in Asterisk 21. Use MixMonitorStartEvent instead.")]
public sealed class MonitorStartEvent : ChannelEventBase
{
}

