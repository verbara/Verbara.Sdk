using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SkypeLicenseList")]
public sealed class SkypeLicenseListAction : ManagerAction, IEventGeneratingAction
{
}

