using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("VoicemailUserEntry")]
public sealed class VoicemailUserEntryEvent : ResponseEvent
{
    public string? VmContext { get; set; }
    public string? Voicemailbox { get; set; }
    public string? Fullname { get; set; }
    public string? Email { get; set; }
    public string? Pager { get; set; }
    public string? ServerEmail { get; set; }
    public string? MailCommand { get; set; }
    public string? Language { get; set; }
    public string? Timezone { get; set; }
    public string? Callback { get; set; }
    public string? Dialout { get; set; }
    public string? ExitContext { get; set; }
    public int? SayDurationMinimum { get; set; }
    public bool? SayEnvelope { get; set; }
    public bool? SayCid { get; set; }
    public bool? AttachMessage { get; set; }
    public string? AttachmentFormat { get; set; }
    public bool? DeleteMessage { get; set; }
    public double? VolumeGain { get; set; }
    public bool? CanReview { get; set; }
    public bool? CallOperator { get; set; }
    public int? MaxMessageCount { get; set; }
    public int? MaxMessageLength { get; set; }
    public int? NewMessageCount { get; set; }
    public int? OldMessageCount { get; set; }
    public string? ImapUser { get; set; }
}

