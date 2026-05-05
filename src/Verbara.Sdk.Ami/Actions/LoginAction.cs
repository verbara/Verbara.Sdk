using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Login")]
public sealed class LoginAction : ManagerAction
{
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string? AuthType { get; set; }
    public string? Key { get; set; }
    public string? Events { get; set; }
}

