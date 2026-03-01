using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SkypeAccountProperty")]
public sealed class SkypeAccountPropertyAction : ManagerAction
{
    public string? User { get; set; }
}

