using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("OriginateSuccess")]
public sealed class OriginateSuccessEvent : ManagerEvent
{
}

