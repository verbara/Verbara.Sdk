using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Challenge")]
public sealed class ChallengeAction : ManagerAction
{
    public string? AuthType { get; set; }
}

