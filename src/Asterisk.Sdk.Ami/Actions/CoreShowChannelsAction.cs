using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("CoreShowChannels")]
public sealed class CoreShowChannelsAction : ManagerAction, IEventGeneratingAction
{
}

