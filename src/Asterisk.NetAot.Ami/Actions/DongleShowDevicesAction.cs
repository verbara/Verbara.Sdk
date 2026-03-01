using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DongleShowDevices")]
public sealed class DongleShowDevicesAction : ManagerAction, IEventGeneratingAction
{
}

