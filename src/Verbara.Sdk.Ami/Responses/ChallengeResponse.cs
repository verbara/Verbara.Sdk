using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("Challenge")]
public sealed class ChallengeResponse : ManagerResponse
{
    public string? Challenge { get; set; }
}

