using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("LoadAverageLimit")]
public sealed class LoadAverageLimitEvent : SecurityEventBase
{
}

