using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FAXSessionsComplete")]
public sealed class FAXSessionsCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
