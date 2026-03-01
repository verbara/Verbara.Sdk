using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("CoreShowChannels")]
public sealed class CoreShowChannelsAction : ManagerAction, IEventGeneratingAction
{
}

