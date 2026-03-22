using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DBGetTreeResponse")]
public sealed class DbGetTreeResponseEvent : ResponseEvent
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
}
