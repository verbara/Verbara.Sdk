using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DbGetResponse")]
public sealed class DbGetResponseEvent : ResponseEvent
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
}

