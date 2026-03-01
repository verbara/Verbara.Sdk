using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("FaxLicenseList")]
public sealed class FaxLicenseListAction : ManagerAction, IEventGeneratingAction
{
}

