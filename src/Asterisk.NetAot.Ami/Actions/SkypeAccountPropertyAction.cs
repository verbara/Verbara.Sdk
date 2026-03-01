using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SkypeAccountProperty")]
public sealed class SkypeAccountPropertyAction : ManagerAction
{
    public string? User { get; set; }
}

