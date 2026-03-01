using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeListRooms")]
public sealed class ConfbridgeListRoomsAction : ManagerAction, IEventGeneratingAction
{
}

