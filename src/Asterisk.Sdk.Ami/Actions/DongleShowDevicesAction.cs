using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DongleShowDevices")]
public sealed class DongleShowDevicesAction : ManagerAction, IEventGeneratingAction
{
}

