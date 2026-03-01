using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("VoicemailUsersList")]
public sealed class VoicemailUsersListAction : ManagerAction, IEventGeneratingAction
{
}

