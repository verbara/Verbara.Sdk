using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "Asterisk 12 renamed this event to QueueMemberPause; use QueueMemberPauseEvent. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("QueueMemberPaused")]
public sealed class QueueMemberPausedEvent : QueueMemberEventBase
{
    public string? Reason { get; set; }
}

