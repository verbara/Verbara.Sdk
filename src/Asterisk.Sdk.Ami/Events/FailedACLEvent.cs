using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FailedACL")]
public sealed class FailedACLEvent : SecurityEventBase
{
    public string? AclName { get; set; }
}

