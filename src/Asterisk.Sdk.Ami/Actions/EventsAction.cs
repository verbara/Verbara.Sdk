using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Events")]
public sealed class EventsAction : ManagerAction
{
    public string? EventMask { get; set; }
}

