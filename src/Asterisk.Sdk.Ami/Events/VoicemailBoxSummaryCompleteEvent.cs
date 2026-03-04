using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("VoicemailBoxSummaryComplete")]
public sealed class VoicemailBoxSummaryCompleteEvent : ResponseEvent
{
}
