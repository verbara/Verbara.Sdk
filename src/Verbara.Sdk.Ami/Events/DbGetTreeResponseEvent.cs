using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DBGetTreeResponse")]
public sealed class DbGetTreeResponseEvent : ResponseEvent
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Val { get; set; }
}
