using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("Challenge")]
public sealed class ChallengeResponse : ManagerResponse
{
    public string? Challenge { get; set; }
}

