using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MonitorStart")]
[Obsolete("app_monitor removed in Asterisk 21. Use MixMonitorStartEvent instead.")]
public sealed class MonitorStartEvent : ChannelEventBase
{
}

