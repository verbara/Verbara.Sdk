using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("Challenge")]
public sealed class ChallengeResponse : ManagerResponse
{
    public string? Challenge { get; set; }
}

