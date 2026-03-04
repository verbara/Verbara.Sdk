using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("VoicemailBoxSummary")]
public sealed class VoicemailBoxSummaryEvent : ResponseEvent
{
    public string? Mailbox { get; set; }
    public string? Context { get; set; }
    public string? Folder { get; set; }
    public string? ID { get; set; }
    public string? CallerIDNum { get; set; }
    public string? CallerIDName { get; set; }
    public string? Origtime { get; set; }
    public string? Duration { get; set; }
    public string? Flag { get; set; }
}
