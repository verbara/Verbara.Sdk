using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ParkedCallsComplete")]
public sealed class ParkedCallsCompleteEvent : ResponseEvent
{
}

