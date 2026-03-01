using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DtmfBegin")]
public sealed class DtmfBeginEvent : ManagerEvent
{
}

