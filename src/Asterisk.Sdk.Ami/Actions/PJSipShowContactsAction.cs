using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PJSIPShowContacts")]
public sealed class PJSipShowContactsAction : ManagerAction, IEventGeneratingAction
{
}

