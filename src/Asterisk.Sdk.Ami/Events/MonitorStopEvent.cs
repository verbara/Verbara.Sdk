using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MonitorStop")]
[Obsolete("app_monitor removed in Asterisk 21. Use MixMonitorStopEvent instead.")]
public sealed class MonitorStopEvent : ChannelEventBase
{
}

