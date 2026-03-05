using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AuthListComplete")]
public sealed class AuthListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
