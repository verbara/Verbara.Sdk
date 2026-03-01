using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("DahdiShowChannels")]
public sealed class DahdiShowChannelsAction : ManagerAction, IEventGeneratingAction
{
}

