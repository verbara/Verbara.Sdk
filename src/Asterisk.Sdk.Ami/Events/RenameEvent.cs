using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Rename")]
public sealed class RenameEvent : ManagerEvent
{
    public string? Channel { get; set; }
}

