using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DbGetResponse")]
public sealed class DbGetResponseEvent : ResponseEvent
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
}

