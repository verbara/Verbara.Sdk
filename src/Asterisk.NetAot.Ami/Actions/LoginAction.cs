using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Login")]
public sealed class LoginAction : ManagerAction
{
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string? AuthType { get; set; }
    public string? Key { get; set; }
    public string? Events { get; set; }
}

