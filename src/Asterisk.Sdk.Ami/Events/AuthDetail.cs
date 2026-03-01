using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AuthDetail")]
public sealed class AuthDetail : ResponseEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Md5Cred { get; set; }
    public string? Realm { get; set; }
    public int? NonceLifetime { get; set; }
    public string? AuthType { get; set; }
    public string? EndpointName { get; set; }
}

