using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SkypeLicenseList")]
public sealed class SkypeLicenseListAction : ManagerAction, IEventGeneratingAction
{
}

