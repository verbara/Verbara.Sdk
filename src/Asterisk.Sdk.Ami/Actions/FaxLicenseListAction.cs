using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("FaxLicenseList")]
public sealed class FaxLicenseListAction : ManagerAction, IEventGeneratingAction
{
}

