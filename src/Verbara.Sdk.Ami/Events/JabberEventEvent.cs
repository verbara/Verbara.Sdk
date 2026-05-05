using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("JabberEvent")]
public sealed class JabberEventEvent : ManagerEvent
{
    public string? Account { get; set; }
    public string? Packet { get; set; }
}

