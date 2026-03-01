using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MWIDelete")]
public sealed class MWIDeleteAction : ManagerAction
{
    public string? Mailbox { get; set; }
}

