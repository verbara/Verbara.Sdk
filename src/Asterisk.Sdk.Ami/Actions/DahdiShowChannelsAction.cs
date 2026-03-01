using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DahdiShowChannels")]
public sealed class DahdiShowChannelsAction : ManagerAction, IEventGeneratingAction
{
}

