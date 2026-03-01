using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("VoicemailUsersList")]
public sealed class VoicemailUsersListAction : ManagerAction, IEventGeneratingAction
{
}

