using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("OriginateFailure")]
public sealed class OriginateFailureEvent : ManagerEvent
{
}

