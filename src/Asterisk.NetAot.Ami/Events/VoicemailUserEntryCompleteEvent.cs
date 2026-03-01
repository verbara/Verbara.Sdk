using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("VoicemailUserEntryComplete")]
public sealed class VoicemailUserEntryCompleteEvent : ResponseEvent
{
}

