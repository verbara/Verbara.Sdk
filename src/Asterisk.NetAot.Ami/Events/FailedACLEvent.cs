using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("FailedACL")]
public sealed class FailedACLEvent : SecurityEventBase
{
    public string? AclName { get; set; }
}

