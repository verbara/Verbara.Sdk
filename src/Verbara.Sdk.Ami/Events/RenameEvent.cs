using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Rename")]
public sealed class RenameEvent : ManagerEvent
{
    public string? Channel { get; set; }
}

