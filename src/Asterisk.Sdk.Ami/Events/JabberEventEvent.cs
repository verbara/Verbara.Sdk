using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("JabberEvent")]
public sealed class JabberEventEvent : ManagerEvent
{
    public string? Account { get; set; }
    public string? Packet { get; set; }
}

