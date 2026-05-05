using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Challenge")]
public sealed class ChallengeAction : ManagerAction
{
    public string? AuthType { get; set; }
}

