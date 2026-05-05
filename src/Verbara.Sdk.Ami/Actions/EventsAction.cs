using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Events")]
public sealed class EventsAction : ManagerAction
{
    public string? EventMask { get; set; }
}

