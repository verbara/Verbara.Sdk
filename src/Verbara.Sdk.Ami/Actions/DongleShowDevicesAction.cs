using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("DongleShowDevices")]
public sealed class DongleShowDevicesAction : ManagerAction, IEventGeneratingAction
{
}

