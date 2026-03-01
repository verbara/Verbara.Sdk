using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ZapShowChannels")]
public sealed class ZapShowChannelsAction : ManagerAction, IEventGeneratingAction
{
}

