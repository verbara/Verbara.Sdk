using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MonitorStop")]
[Obsolete("app_monitor removed in Asterisk 21. Use MixMonitorStopEvent instead.")]
public sealed class MonitorStopEvent : ChannelEventBase
{
}

