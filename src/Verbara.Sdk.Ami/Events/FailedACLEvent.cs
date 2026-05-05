using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("FailedACL")]
public sealed class FailedACLEvent : SecurityEventBase
{
    public string? AclName { get; set; }
}

