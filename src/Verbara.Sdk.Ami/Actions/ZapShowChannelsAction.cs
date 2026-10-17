using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "Asterisk 1.6 removed the Zaptel action ZapShowChannels; chan_dahdi registers DAHDIShowChannels instead, and no supported Asterisk accepts ZapShowChannels. Use DahdiShowChannelsAction. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("ZapShowChannels")]
public sealed class ZapShowChannelsAction : ManagerAction, IEventGeneratingAction
{
}

