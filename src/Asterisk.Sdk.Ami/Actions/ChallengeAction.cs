using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Challenge")]
public sealed class ChallengeAction : ManagerAction
{
    public string? AuthType { get; set; }
}

